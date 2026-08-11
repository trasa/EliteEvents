package controller

import (
	"context"
	"fmt"
	"time"

	. "github.com/onsi/ginkgo/v2"
	. "github.com/onsi/gomega"
	appsv1 "k8s.io/api/apps/v1"
	batchv1 "k8s.io/api/batch/v1"
	corev1 "k8s.io/api/core/v1"
	apierrors "k8s.io/apimachinery/pkg/api/errors"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/types"
	"sigs.k8s.io/controller-runtime/pkg/client"
	"sigs.k8s.io/controller-runtime/pkg/reconcile"

	elitev1alpha1 "github.com/trasa/EliteEvents/operator/api/v1alpha1"
)

// stubProbe stands in for the consumers' /health/stream endpoint; no pods run under envtest.
type stubProbe struct {
	status *StreamStatus
	err    error
}

func (s *stubProbe) Probe(context.Context, string) (*StreamStatus, error) {
	return s.status, s.err
}

var _ = Describe("FeedListener Controller", func() {
	const namespace = "default"

	var (
		ctx        context.Context
		name       string
		key        types.NamespacedName
		reconciler *FeedListenerReconciler
	)

	// reconcileUntilStable drives the loop the way the manager would, and re-running a settled
	// reconcile also exercises idempotence. A single pass is now enough to create everything —
	// see "creates every child on the first pass" below, which pins that directly.
	reconcileUntilStable := func() {
		GinkgoHelper()
		for i := 0; i < 5; i++ {
			_, err := reconciler.Reconcile(ctx, reconcile.Request{NamespacedName: key})
			Expect(err).NotTo(HaveOccurred())
		}
	}

	newFeedListener := func(consumers int32) *elitev1alpha1.FeedListener {
		return &elitev1alpha1.FeedListener{
			ObjectMeta: metav1.ObjectMeta{Name: name, Namespace: namespace},
			Spec: elitev1alpha1.FeedListenerSpec{
				RelayEndpoint:         "tcp://eddn.edcd.io:9500",
				Consumers:             consumers,
				Image:                 "registry.digitalocean.com/meancat/elite-ingestion:test",
				ReconnectAfterSilence: metav1.Duration{Duration: 2 * time.Minute},
				Redis: elitev1alpha1.RedisConfig{
					ConnectionString: "redis:6379,abortConnect=false",
					AuthSecret:       &elitev1alpha1.RedisAuthSecret{Name: "redis-auth", Key: "password"},
				},
			},
		}
	}

	BeforeEach(func() {
		ctx = context.Background()
		name = fmt.Sprintf("feed-%d", time.Now().UnixNano())
		key = types.NamespacedName{Name: name, Namespace: namespace}
		reconciler = &FeedListenerReconciler{
			Client:      k8sClient,
			Scheme:      k8sClient.Scheme(),
			StreamProbe: &stubProbe{status: &StreamStatus{}},
		}
	})

	Context("when reconciling a feed", func() {
		It("creates a ConfigMap, a Service and one Deployment per shard", func() {
			Expect(k8sClient.Create(ctx, newFeedListener(3))).To(Succeed())
			reconcileUntilStable()

			var cm corev1.ConfigMap
			Expect(k8sClient.Get(ctx, types.NamespacedName{Name: name + "-config", Namespace: namespace}, &cm)).To(Succeed())
			Expect(cm.Data).To(HaveKeyWithValue("Eddn__ShardCount", "3"))
			Expect(cm.Data).To(HaveKeyWithValue("Eddn__StreamUrl", "tcp://eddn.edcd.io:9500"))
			// The .NET TimeSpan format, not Go's "2m0s".
			Expect(cm.Data).To(HaveKeyWithValue("Eddn__ReconnectAfterSilence", "00:02:00"))

			var svc corev1.Service
			Expect(k8sClient.Get(ctx, key, &svc)).To(Succeed())

			indexes := map[string]bool{}
			for shard := 0; shard < 3; shard++ {
				var deploy appsv1.Deployment
				deployKey := types.NamespacedName{Name: fmt.Sprintf("%s-%d", name, shard), Namespace: namespace}
				Expect(k8sClient.Get(ctx, deployKey, &deploy)).To(Succeed())

				Expect(*deploy.Spec.Replicas).To(BeEquivalentTo(1),
					"a shard must never run two pods; the overlap double-counts every event")
				Expect(deploy.Spec.Strategy.Type).To(Equal(appsv1.RecreateDeploymentStrategyType))

				for _, env := range deploy.Spec.Template.Spec.Containers[0].Env {
					if env.Name == "Eddn__ShardIndex" {
						indexes[env.Value] = true
					}
				}
			}
			Expect(indexes).To(HaveLen(3), "each shard must get a distinct index")
		})

		It("owner-references every child so Kubernetes collects them", func() {
			Expect(k8sClient.Create(ctx, newFeedListener(1))).To(Succeed())
			reconcileUntilStable()

			var deploy appsv1.Deployment
			Expect(k8sClient.Get(ctx, types.NamespacedName{Name: name + "-0", Namespace: namespace}, &deploy)).To(Succeed())
			Expect(deploy.OwnerReferences).To(HaveLen(1))
			Expect(deploy.OwnerReferences[0].Kind).To(Equal("FeedListener"))
			Expect(*deploy.OwnerReferences[0].Controller).To(BeTrue())
		})

		It("registers the finalizer before creating anything", func() {
			Expect(k8sClient.Create(ctx, newFeedListener(1))).To(Succeed())
			reconcileUntilStable()

			var fl elitev1alpha1.FeedListener
			Expect(k8sClient.Get(ctx, key, &fl)).To(Succeed())
			Expect(fl.Finalizers).To(ContainElement(elitev1alpha1.FeedListenerFinalizer))
		})

		// Scaling down is where owner references stop helping: the pruned shards' owner is
		// still alive, so nothing garbage-collects them and they keep writing.
		It("prunes shards that fall outside a reduced consumer count", func() {
			Expect(k8sClient.Create(ctx, newFeedListener(4))).To(Succeed())
			reconcileUntilStable()

			var fl elitev1alpha1.FeedListener
			Expect(k8sClient.Get(ctx, key, &fl)).To(Succeed())
			fl.Spec.Consumers = 2
			Expect(k8sClient.Update(ctx, &fl)).To(Succeed())
			reconcileUntilStable()

			for shard := 0; shard < 2; shard++ {
				var deploy appsv1.Deployment
				deployKey := types.NamespacedName{Name: fmt.Sprintf("%s-%d", name, shard), Namespace: namespace}
				Expect(k8sClient.Get(ctx, deployKey, &deploy)).To(Succeed(), "shard %d should survive", shard)
			}
			for shard := 2; shard < 4; shard++ {
				var deploy appsv1.Deployment
				deployKey := types.NamespacedName{Name: fmt.Sprintf("%s-%d", name, shard), Namespace: namespace}
				err := k8sClient.Get(ctx, deployKey, &deploy)
				Expect(apierrors.IsNotFound(err)).To(BeTrue(), "shard %d should have been pruned", shard)
			}

			Expect(k8sClient.Get(ctx, types.NamespacedName{Name: name + "-config", Namespace: namespace}, &corev1.ConfigMap{})).To(Succeed())
		})

		It("rolls the consumers when the feed configuration changes", func() {
			Expect(k8sClient.Create(ctx, newFeedListener(1))).To(Succeed())
			reconcileUntilStable()

			deployKey := types.NamespacedName{Name: name + "-0", Namespace: namespace}
			var before appsv1.Deployment
			Expect(k8sClient.Get(ctx, deployKey, &before)).To(Succeed())

			var fl elitev1alpha1.FeedListener
			Expect(k8sClient.Get(ctx, key, &fl)).To(Succeed())
			fl.Spec.RelayEndpoint = "tcp://relay.example.org:9500"
			Expect(k8sClient.Update(ctx, &fl)).To(Succeed())
			reconcileUntilStable()

			var after appsv1.Deployment
			Expect(k8sClient.Get(ctx, deployKey, &after)).To(Succeed())
			Expect(after.Spec.Template.Annotations[configHashAnnotation]).
				NotTo(Equal(before.Spec.Template.Annotations[configHashAnnotation]),
					"a ConfigMap edit alone does not restart pods; the hash annotation is what rolls them")
		})

		It("reports status from the consumers", func() {
			Expect(k8sClient.Create(ctx, newFeedListener(1))).To(Succeed())
			reconcileUntilStable()

			var fl elitev1alpha1.FeedListener
			Expect(k8sClient.Get(ctx, key, &fl)).To(Succeed())
			Expect(fl.Status.ObservedGeneration).To(Equal(fl.Generation))
			Expect(fl.Status.DesiredConsumers).To(BeEquivalentTo(1))
			// No pods exist under envtest, so the feed is correctly reported as not yet up.
			Expect(fl.Status.Phase).To(Equal("Pending"))
		})

		// The finalizer is registered by a metadata write, which does not bump generation and is
		// therefore filtered by feedListenerTriggers. Reconcile has to finish the job in the same
		// pass; if it goes back to returning early after registering, a real deploy would leave a
		// finalizer, no children, and nothing scheduled to wake it. Reconciling exactly once is
		// the whole point of this test — do not swap it for reconcileUntilStable.
		It("creates every child on the first pass, without a second event", func() {
			Expect(k8sClient.Create(ctx, newFeedListener(2))).To(Succeed())

			_, err := reconciler.Reconcile(ctx, reconcile.Request{NamespacedName: key})
			Expect(err).NotTo(HaveOccurred())

			var fl elitev1alpha1.FeedListener
			Expect(k8sClient.Get(ctx, key, &fl)).To(Succeed())
			Expect(fl.Finalizers).To(ContainElement(elitev1alpha1.FeedListenerFinalizer))

			var cm corev1.ConfigMap
			Expect(k8sClient.Get(ctx, types.NamespacedName{Name: name + "-config", Namespace: namespace}, &cm)).
				To(Succeed(), "the first pass must register the finalizer AND build the children")
			var svc corev1.Service
			Expect(k8sClient.Get(ctx, key, &svc)).To(Succeed())
			for shard := 0; shard < 2; shard++ {
				var deploy appsv1.Deployment
				deployKey := types.NamespacedName{Name: fmt.Sprintf("%s-%d", name, shard), Namespace: namespace}
				Expect(k8sClient.Get(ctx, deployKey, &deploy)).To(Succeed())
			}
		})

		// The premise feedListenerTriggers rests on: everything a settled reconcile writes goes to
		// the status subresource, which never bumps generation. If a future change starts writing
		// spec or labels from reconcileStatus, generation would move on every poll, the predicate
		// would admit every one of those events, and the self-triggering loop is back.
		It("writes nothing on a settled pass that would re-trigger the watch", func() {
			Expect(k8sClient.Create(ctx, newFeedListener(1))).To(Succeed())
			reconcileUntilStable()

			var before elitev1alpha1.FeedListener
			Expect(k8sClient.Get(ctx, key, &before)).To(Succeed())

			_, err := reconciler.Reconcile(ctx, reconcile.Request{NamespacedName: key})
			Expect(err).NotTo(HaveOccurred())

			var after elitev1alpha1.FeedListener
			Expect(k8sClient.Get(ctx, key, &after)).To(Succeed())

			Expect(after.Generation).To(Equal(before.Generation),
				"a poll that bumps generation makes the controller trigger itself on its own status write")
			Expect(after.Spec).To(Equal(before.Spec))
			Expect(after.Finalizers).To(Equal(before.Finalizers))
		})

		// The requeue is the only thing left driving the poll once the watch stops delivering the
		// controller's own status writes back to it.
		It("asks to be requeued so feed health keeps being polled", func() {
			Expect(k8sClient.Create(ctx, newFeedListener(1))).To(Succeed())
			reconcileUntilStable()

			result, err := reconciler.Reconcile(ctx, reconcile.Request{NamespacedName: key})
			Expect(err).NotTo(HaveOccurred())
			Expect(result.RequeueAfter).To(Equal(statusPollInterval),
				"nothing else wakes the controller for feed health; a relay going quiet changes nothing in the API")
		})
	})

	Context("when scheduling index maintenance", func() {
		cronJobKey := func() types.NamespacedName {
			return types.NamespacedName{Name: name + "-rebuild", Namespace: namespace}
		}

		// The schedule arrives by API-server defaulting, not from anything the test sets. That
		// is the production path too: k8s/25-feedlistener.yaml does not mention indexMaintenance.
		It("creates a rebuild CronJob by default and switches the in-process timer off", func() {
			Expect(k8sClient.Create(ctx, newFeedListener(2))).To(Succeed())
			reconcileUntilStable()

			var fl elitev1alpha1.FeedListener
			Expect(k8sClient.Get(ctx, key, &fl)).To(Succeed())
			Expect(fl.Spec.IndexMaintenance).NotTo(BeNil(), "defaulting should have filled this in")
			Expect(fl.Spec.IndexMaintenance.Schedule).To(HaveValue(Not(BeEmpty())))

			var cronJob batchv1.CronJob
			Expect(k8sClient.Get(ctx, cronJobKey(), &cronJob)).To(Succeed())
			Expect(cronJob.Spec.Schedule).To(Equal(*fl.Spec.IndexMaintenance.Schedule))
			Expect(cronJob.Spec.JobTemplate.Spec.Template.Spec.Containers[0].Args).
				To(ContainElement("--rebuild-indexes"))

			var cm corev1.ConfigMap
			Expect(k8sClient.Get(ctx, types.NamespacedName{
				Name: name + "-config", Namespace: namespace}, &cm)).To(Succeed())
			Expect(cm.Data).To(HaveKeyWithValue("IndexMaintenance__Periodic", "false"),
				"the CronJob owns the schedule, so the consumers must not also run one")
		})

		// Clearing the schedule hands upkeep back to shard 0. A CronJob left behind would keep
		// firing alongside it — a duplicate full keyspace scan every tick.
		It("removes the CronJob when the schedule is cleared", func() {
			Expect(k8sClient.Create(ctx, newFeedListener(1))).To(Succeed())
			reconcileUntilStable()
			Expect(k8sClient.Get(ctx, cronJobKey(), &batchv1.CronJob{})).To(Succeed())

			var fl elitev1alpha1.FeedListener
			Expect(k8sClient.Get(ctx, key, &fl)).To(Succeed())
			// An explicit empty string, which only survives the round-trip because Schedule is a
			// pointer — with a plain string the API server would re-apply the default here.
			fl.Spec.IndexMaintenance.Schedule = ptr("")
			Expect(k8sClient.Update(ctx, &fl)).To(Succeed())
			reconcileUntilStable()

			Expect(k8sClient.Get(ctx, key, &fl)).To(Succeed())
			Expect(fl.Spec.IndexMaintenance.Schedule).To(HaveValue(BeEmpty()),
				"the cleared schedule must not be re-defaulted, or it could never be turned off")

			err := k8sClient.Get(ctx, cronJobKey(), &batchv1.CronJob{})
			Expect(apierrors.IsNotFound(err)).To(BeTrue(), "the CronJob should have been removed")

			var cm corev1.ConfigMap
			Expect(k8sClient.Get(ctx, types.NamespacedName{
				Name: name + "-config", Namespace: namespace}, &cm)).To(Succeed())
			Expect(cm.Data).To(HaveKeyWithValue("IndexMaintenance__Periodic", "true"),
				"with no CronJob, the consumers must take the schedule back")
		})

		It("reports the last successful rebuild on status", func() {
			Expect(k8sClient.Create(ctx, newFeedListener(1))).To(Succeed())
			reconcileUntilStable()

			var cronJob batchv1.CronJob
			Expect(k8sClient.Get(ctx, cronJobKey(), &cronJob)).To(Succeed())
			ranAt := metav1.NewTime(time.Now().Add(-time.Minute).Truncate(time.Second))
			cronJob.Status.LastSuccessfulTime = &ranAt
			Expect(k8sClient.Status().Update(ctx, &cronJob)).To(Succeed())

			reconcileUntilStable()

			var fl elitev1alpha1.FeedListener
			Expect(k8sClient.Get(ctx, key, &fl)).To(Succeed())
			Expect(fl.Status.LastIndexRebuildTime).NotTo(BeNil())
			Expect(fl.Status.LastIndexRebuildTime.Time).To(BeTemporally("==", ranAt.Time))
		})
	})

	Context("when deleting a feed", func() {
		// A rebuild racing the purge would not merely dirty the indexes, it would rewrite them
		// wholesale — the purge would appear to succeed and the keys would be back seconds later.
		It("stops the maintenance CronJob and waits for an in-flight rebuild", func() {
			Expect(k8sClient.Create(ctx, newFeedListener(1))).To(Succeed())
			reconcileUntilStable()

			cronJobKey := types.NamespacedName{Name: name + "-rebuild", Namespace: namespace}
			drainKey := types.NamespacedName{Name: name + "-drain", Namespace: namespace}
			Expect(k8sClient.Get(ctx, cronJobKey, &batchv1.CronJob{})).To(Succeed())

			// A rebuild pod mid-pass. Deleting the CronJob does not stop one already running, and
			// it is the dangerous case: a rebuild writes the whole index, so it would not dirty
			// the purge's result, it would restore it wholesale.
			rebuildPod := &corev1.Pod{
				ObjectMeta: metav1.ObjectMeta{
					Name:      name + "-rebuild-inflight",
					Namespace: namespace,
					Labels: map[string]string{
						nameLabel:      "feed-maintenance",
						instanceLabel:  name,
						partOfLabel:    "elite-events",
						componentLabel: "maintenance",
					},
				},
				Spec: corev1.PodSpec{Containers: []corev1.Container{{
					Name: "rebuild", Image: "elite-ingestion:test",
				}}},
			}
			Expect(k8sClient.Create(ctx, rebuildPod)).To(Succeed())

			var fl elitev1alpha1.FeedListener
			Expect(k8sClient.Get(ctx, key, &fl)).To(Succeed())
			Expect(k8sClient.Delete(ctx, &fl)).To(Succeed())

			_, err := reconciler.Reconcile(ctx, reconcile.Request{NamespacedName: key})
			Expect(err).NotTo(HaveOccurred())

			err = k8sClient.Get(ctx, cronJobKey, &batchv1.CronJob{})
			Expect(apierrors.IsNotFound(err)).To(BeTrue(),
				"the CronJob must be gone before the purge runs, or a tick could undo it")

			err = k8sClient.Get(ctx, drainKey, &batchv1.Job{})
			Expect(apierrors.IsNotFound(err)).To(BeTrue(),
				"the purge must not start while a rebuild pod is still running")

			// Once it exits, teardown proceeds.
			Expect(k8sClient.Delete(ctx, rebuildPod, client.GracePeriodSeconds(0))).To(Succeed())
			_, err = reconciler.Reconcile(ctx, reconcile.Request{NamespacedName: key})
			Expect(err).NotTo(HaveOccurred())
			Expect(k8sClient.Get(ctx, drainKey, &batchv1.Job{})).To(Succeed())
		})

		// The ordering here is the whole reason the finalizer exists: purging while a consumer
		// is still subscribed would be undone by the next write it makes.
		It("stops the consumers before starting the drain, then releases", func() {
			Expect(k8sClient.Create(ctx, newFeedListener(2))).To(Succeed())
			reconcileUntilStable()

			var fl elitev1alpha1.FeedListener
			Expect(k8sClient.Get(ctx, key, &fl)).To(Succeed())
			Expect(k8sClient.Delete(ctx, &fl)).To(Succeed())

			_, err := reconciler.Reconcile(ctx, reconcile.Request{NamespacedName: key})
			Expect(err).NotTo(HaveOccurred())

			var shards appsv1.DeploymentList
			Expect(k8sClient.List(ctx, &shards,
				client.InNamespace(namespace),
				client.MatchingLabels(map[string]string{instanceLabel: name}),
			)).To(Succeed())
			Expect(shards.Items).To(BeEmpty(), "consumers must be stopped before the purge runs")

			// With the consumers gone the next pass creates the drain Job.
			_, err = reconciler.Reconcile(ctx, reconcile.Request{NamespacedName: key})
			Expect(err).NotTo(HaveOccurred())

			var job batchv1.Job
			jobKey := types.NamespacedName{Name: name + "-drain", Namespace: namespace}
			Expect(k8sClient.Get(ctx, jobKey, &job)).To(Succeed())
			Expect(job.Spec.Template.Spec.Containers[0].Args).To(ContainElement("--purge-indexes"))
			Expect(job.Spec.Template.Spec.Containers[0].Image).To(Equal(fl.Spec.Image))

			// The resource must still exist: the finalizer holds it until the drain reports back.
			Expect(k8sClient.Get(ctx, key, &fl)).To(Succeed())
			Expect(fl.Finalizers).To(ContainElement(elitev1alpha1.FeedListenerFinalizer))

			job.Status.Succeeded = 1
			Expect(k8sClient.Status().Update(ctx, &job)).To(Succeed())

			_, err = reconciler.Reconcile(ctx, reconcile.Request{NamespacedName: key})
			Expect(err).NotTo(HaveOccurred())

			err = k8sClient.Get(ctx, key, &fl)
			Expect(apierrors.IsNotFound(err)).To(BeTrue(), "the finalizer should have been released")
		})

		It("skips the drain when retainIndexesOnDelete is set", func() {
			fl := newFeedListener(1)
			fl.Spec.RetainIndexesOnDelete = true
			Expect(k8sClient.Create(ctx, fl)).To(Succeed())
			reconcileUntilStable()

			Expect(k8sClient.Get(ctx, key, fl)).To(Succeed())
			Expect(k8sClient.Delete(ctx, fl)).To(Succeed())

			_, err := reconciler.Reconcile(ctx, reconcile.Request{NamespacedName: key})
			Expect(err).NotTo(HaveOccurred())

			err = k8sClient.Get(ctx, key, fl)
			Expect(apierrors.IsNotFound(err)).To(BeTrue())

			err = k8sClient.Get(ctx, types.NamespacedName{Name: name + "-drain", Namespace: namespace}, &batchv1.Job{})
			Expect(apierrors.IsNotFound(err)).To(BeTrue(), "no drain Job should have been created")
		})

		// A finalizer that cannot be satisfied wedges the resource in Terminating forever and
		// blocks namespace deletion, which is worse than the stale keys it was protecting.
		It("releases the finalizer even when the drain fails", func() {
			Expect(k8sClient.Create(ctx, newFeedListener(1))).To(Succeed())
			reconcileUntilStable()

			var fl elitev1alpha1.FeedListener
			Expect(k8sClient.Get(ctx, key, &fl)).To(Succeed())
			Expect(k8sClient.Delete(ctx, &fl)).To(Succeed())

			for i := 0; i < 2; i++ {
				_, err := reconciler.Reconcile(ctx, reconcile.Request{NamespacedName: key})
				Expect(err).NotTo(HaveOccurred())
			}

			var job batchv1.Job
			jobKey := types.NamespacedName{Name: name + "-drain", Namespace: namespace}
			Expect(k8sClient.Get(ctx, jobKey, &job)).To(Succeed())

			// The API server rejects a Failed=True Job without FailureTarget and a start time,
			// so this mirrors exactly what the Job controller writes on a real failure.
			now := metav1.Now()
			job.Status.StartTime = &now
			job.Status.Conditions = []batchv1.JobCondition{
				{
					Type:               batchv1.JobFailureTarget,
					Status:             corev1.ConditionTrue,
					Reason:             "BackoffLimitExceeded",
					LastTransitionTime: now,
				},
				{
					Type:               batchv1.JobFailed,
					Status:             corev1.ConditionTrue,
					Reason:             "BackoffLimitExceeded",
					LastTransitionTime: now,
				},
			}
			Expect(k8sClient.Status().Update(ctx, &job)).To(Succeed())

			_, err := reconciler.Reconcile(ctx, reconcile.Request{NamespacedName: key})
			Expect(err).NotTo(HaveOccurred())

			err = k8sClient.Get(ctx, key, &fl)
			Expect(apierrors.IsNotFound(err)).To(BeTrue(),
				"a failed drain must not leave the resource stuck in Terminating")
		})
	})
})
