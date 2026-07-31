package controller

import (
	"context"
	"fmt"
	"time"

	appsv1 "k8s.io/api/apps/v1"
	batchv1 "k8s.io/api/batch/v1"
	corev1 "k8s.io/api/core/v1"
	apierrors "k8s.io/apimachinery/pkg/api/errors"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/types"
	ctrl "sigs.k8s.io/controller-runtime"
	"sigs.k8s.io/controller-runtime/pkg/client"
	"sigs.k8s.io/controller-runtime/pkg/controller/controllerutil"
	logf "sigs.k8s.io/controller-runtime/pkg/log"

	elitev1alpha1 "github.com/trasa/EliteEvents/operator/api/v1alpha1"
)

const (
	// drainJobBackoffLimit is how many times the purge is retried before the finalizer gives up.
	drainJobBackoffLimit = 3

	// drainPollInterval is how often teardown progress is re-checked.
	drainPollInterval = 5 * time.Second
)

func drainJobName(fl *elitev1alpha1.FeedListener) string { return fl.Name + "-drain" }

// drainLabels deliberately do not match selectorLabels. The drain pod must be invisible to the
// consumer selector: stopConsumers waits for that selector to return no pods, so labelling the
// drain pod as a consumer would make it wait for itself and hang the deletion forever.
func drainLabels(fl *elitev1alpha1.FeedListener) map[string]string {
	return map[string]string{
		nameLabel:      "feed-drain",
		instanceLabel:  fl.Name,
		partOfLabel:    "elite-events",
		componentLabel: "drain",
	}
}

// finalize tears a feed down in order, and the order is the entire point.
//
// The state that outlives this resource is in Redis, not Kubernetes: index:systems and
// index:carriers carry no TTL by design, because ZRANGEBYLEX requires every member at score 0
// and a TTL on the key would drop the whole index. They are kept honest only by the listener
// that writes them, so deleting the listener without draining leaves keys that nothing will
// ever reclaim.
//
// Purging cannot simply run alongside the writers, either. Every docking write adds index members
// and the maintenance CronJob rebuilds both indexes wholesale, so a purge racing either one would
// be undone — within the second by a consumer, entirely by a rebuild. Both must be gone first,
// and only the controller can sequence that: owner-reference garbage collection is unordered and
// would delete the children and the resource concurrently.
func (r *FeedListenerReconciler) finalize(ctx context.Context, fl *elitev1alpha1.FeedListener) (ctrl.Result, error) {
	log := logf.FromContext(ctx)

	if !controllerutil.ContainsFinalizer(fl, elitev1alpha1.FeedListenerFinalizer) {
		return ctrl.Result{}, nil
	}

	if fl.Status.Phase != "Terminating" {
		fl.Status.Phase = "Terminating"
		if err := r.Status().Update(ctx, fl); err != nil && !apierrors.IsConflict(err) {
			log.Error(err, "recording terminating phase")
		}
	}

	if fl.Spec.RetainIndexesOnDelete {
		log.Info("retainIndexesOnDelete is set; releasing without draining Redis")
		r.eventf(fl, corev1.EventTypeNormal, "DrainSkipped",
			"retainIndexesOnDelete is set; Redis search indexes were left in place")
		return r.releaseFinalizer(ctx, fl)
	}

	// Step 1 — stop everything that writes the indexes. Deleting rather than scaling to zero
	// keeps this idempotent and leaves nothing behind if the purge later fails.
	stopped, err := r.stopWriters(ctx, fl)
	if err != nil {
		return ctrl.Result{}, err
	}
	if !stopped {
		log.V(1).Info("waiting for writers to terminate before draining")
		return ctrl.Result{RequeueAfter: drainPollInterval}, nil
	}

	// Step 2 — purge, in a Job that runs the ingestion image itself. The controller never
	// speaks Redis: key formats live in RedisKeys and nowhere else, and reimplementing them in
	// Go would put the one thing both containers must agree on into a third language.
	job, err := r.ensureDrainJob(ctx, fl)
	if err != nil {
		return ctrl.Result{}, err
	}

	switch {
	case job.Status.Succeeded > 0:
		log.Info("drain complete; releasing finalizer")
		r.eventf(fl, corev1.EventTypeNormal, "Drained", "Redis search indexes purged")
		return r.releaseFinalizer(ctx, fl)

	case jobFailed(job):
		// Refusing to release here would wedge the resource in Terminating forever, and a
		// finalizer that cannot be satisfied is worse than leftover keys: it blocks the
		// namespace and needs a hand-edit to clear. Surface it loudly and let go.
		log.Error(fmt.Errorf("drain job %s failed", job.Name),
			"releasing finalizer despite failed drain; Redis indexes may be stale")
		r.eventf(fl, corev1.EventTypeWarning, "DrainFailed",
			"Drain job %s failed after %d attempts; Redis search indexes may be left behind",
			job.Name, drainJobBackoffLimit)
		return r.releaseFinalizer(ctx, fl)

	default:
		return ctrl.Result{RequeueAfter: drainPollInterval}, nil
	}
}

// stopWriters removes everything that can put members back into the indexes and reports whether
// all of it is actually gone.
//
// That is two populations, not one. The consumers write index members on every docking, and the
// maintenance CronJob rebuilds both indexes wholesale — either one running during the purge would
// undo it, and a rebuild would undo it completely rather than partially. Owner-reference garbage
// collection cannot be relied on to remove the CronJob first: it is unordered, and it would be
// racing the drain Job this same finalizer is about to start.
func (r *FeedListenerReconciler) stopWriters(ctx context.Context, fl *elitev1alpha1.FeedListener) (bool, error) {
	// Delete the CronJob before the Deployments. It is the cheaper of the two to stop and the
	// only one that could otherwise start a *new* writer while we wait for the others to exit.
	cronJob := &batchv1.CronJob{}
	cronJob.Name = maintenanceCronJobName(fl)
	cronJob.Namespace = fl.Namespace
	if err := r.Delete(ctx, cronJob); err != nil && !apierrors.IsNotFound(err) {
		return false, fmt.Errorf("stopping maintenance cronjob: %w", err)
	}

	var deployments appsv1.DeploymentList
	if err := r.List(ctx, &deployments,
		client.InNamespace(fl.Namespace),
		client.MatchingLabels(selectorLabels(fl)),
	); err != nil {
		return false, fmt.Errorf("listing shards during drain: %w", err)
	}

	for i := range deployments.Items {
		deploy := &deployments.Items[i]
		if deploy.DeletionTimestamp != nil {
			continue
		}
		if err := r.Delete(ctx, deploy); err != nil && !apierrors.IsNotFound(err) {
			return false, fmt.Errorf("stopping shard %s: %w", deploy.Name, err)
		}
	}

	// Deleting a CronJob does not stop a rebuild already in flight, so any Job it spawned is
	// deleted explicitly. Foreground propagation is what makes the pod wait below meaningful:
	// the default orphans the pods, which would leave a rebuild running against the Redis we are
	// about to purge while its Job disappears from under it.
	var jobs batchv1.JobList
	if err := r.List(ctx, &jobs,
		client.InNamespace(fl.Namespace),
		client.MatchingLabels(maintenanceLabels(fl)),
	); err != nil {
		return false, fmt.Errorf("listing maintenance jobs during drain: %w", err)
	}
	for i := range jobs.Items {
		job := &jobs.Items[i]
		if job.DeletionTimestamp != nil {
			continue
		}
		if err := r.Delete(ctx, job, client.PropagationPolicy(metav1.DeletePropagationForeground)); err != nil &&
			!apierrors.IsNotFound(err) {
			return false, fmt.Errorf("stopping maintenance job %s: %w", job.Name, err)
		}
	}

	// Deletion returns before pods are gone, and a pod still in Terminating is still holding a
	// socket and still writing. Both label sets are checked; they are deliberately disjoint, and
	// neither matches the drain pod that runs next — waiting on that one would hang forever.
	for _, labels := range []map[string]string{selectorLabels(fl), maintenanceLabels(fl)} {
		var pods corev1.PodList
		if err := r.List(ctx, &pods,
			client.InNamespace(fl.Namespace),
			client.MatchingLabels(labels),
		); err != nil {
			return false, fmt.Errorf("listing writer pods during drain: %w", err)
		}
		if len(pods.Items) > 0 {
			return false, nil
		}
	}
	return true, nil
}

// ensureDrainJob creates the purge Job if it does not exist and returns its current state.
// The Job is created once and never updated: a Job's pod template is immutable, and rewriting
// it on each reconcile would fail every time.
func (r *FeedListenerReconciler) ensureDrainJob(ctx context.Context, fl *elitev1alpha1.FeedListener) (*batchv1.Job, error) {
	var job batchv1.Job
	err := r.Get(ctx, types.NamespacedName{Name: drainJobName(fl), Namespace: fl.Namespace}, &job)
	if err == nil {
		return &job, nil
	}
	if !apierrors.IsNotFound(err) {
		return nil, fmt.Errorf("reading drain job: %w", err)
	}

	desired := BuildDrainJob(fl)
	if err := controllerutil.SetControllerReference(fl, desired, r.Scheme); err != nil {
		return nil, err
	}
	if err := r.Create(ctx, desired); err != nil && !apierrors.IsAlreadyExists(err) {
		return nil, fmt.Errorf("creating drain job: %w", err)
	}
	r.eventf(fl, corev1.EventTypeNormal, "Draining",
		"Purging Redis search indexes via job %s", desired.Name)
	return desired, nil
}

func (r *FeedListenerReconciler) releaseFinalizer(ctx context.Context, fl *elitev1alpha1.FeedListener) (ctrl.Result, error) {
	controllerutil.RemoveFinalizer(fl, elitev1alpha1.FeedListenerFinalizer)
	if err := r.Update(ctx, fl); err != nil && !apierrors.IsNotFound(err) {
		return ctrl.Result{}, fmt.Errorf("releasing finalizer: %w", err)
	}
	return ctrl.Result{}, nil
}

func jobFailed(job *batchv1.Job) bool {
	if job.Status.Failed > drainJobBackoffLimit {
		return true
	}
	for _, cond := range job.Status.Conditions {
		if cond.Type == batchv1.JobFailed && cond.Status == corev1.ConditionTrue {
			return true
		}
	}
	return false
}

// BuildDrainJob renders the purge Job — the ingestion image run as a command, exactly like the
// scheduled rebuild; see oneShotPodSpec for why both reuse the consumer image and ConfigMap.
func BuildDrainJob(fl *elitev1alpha1.FeedListener) *batchv1.Job {
	return &batchv1.Job{
		ObjectMeta: metav1.ObjectMeta{
			Name:      drainJobName(fl),
			Namespace: fl.Namespace,
			Labels:    drainLabels(fl),
		},
		Spec: batchv1.JobSpec{
			BackoffLimit: ptr(int32(drainJobBackoffLimit)),
			Template: corev1.PodTemplateSpec{
				ObjectMeta: metav1.ObjectMeta{Labels: drainLabels(fl)},
				Spec:       oneShotPodSpec(fl, "drain", "--purge-indexes"),
			},
		},
	}
}
