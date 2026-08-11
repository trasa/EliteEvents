package controller

import (
	"context"
	"fmt"

	appsv1 "k8s.io/api/apps/v1"
	batchv1 "k8s.io/api/batch/v1"
	corev1 "k8s.io/api/core/v1"
	apierrors "k8s.io/apimachinery/pkg/api/errors"
	"k8s.io/apimachinery/pkg/runtime"
	"k8s.io/client-go/tools/events"
	ctrl "sigs.k8s.io/controller-runtime"
	"sigs.k8s.io/controller-runtime/pkg/builder"
	"sigs.k8s.io/controller-runtime/pkg/client"
	"sigs.k8s.io/controller-runtime/pkg/controller/controllerutil"
	"sigs.k8s.io/controller-runtime/pkg/event"
	logf "sigs.k8s.io/controller-runtime/pkg/log"
	"sigs.k8s.io/controller-runtime/pkg/predicate"

	elitev1alpha1 "github.com/trasa/EliteEvents/operator/api/v1alpha1"
)

// FeedListenerReconciler reconciles a FeedListener object
type FeedListenerReconciler struct {
	client.Client
	Scheme   *runtime.Scheme
	Recorder events.EventRecorder

	// StreamProbe reports feed health from a consumer pod. Injected so tests can run the
	// controller without standing up an HTTP server.
	StreamProbe StreamProbe
}

// +kubebuilder:rbac:groups=elite.meancat.com,resources=feedlisteners,verbs=get;list;watch;create;update;patch;delete
// +kubebuilder:rbac:groups=elite.meancat.com,resources=feedlisteners/status,verbs=get;update;patch
// +kubebuilder:rbac:groups=elite.meancat.com,resources=feedlisteners/finalizers,verbs=update
// +kubebuilder:rbac:groups=apps,resources=deployments,verbs=get;list;watch;create;update;patch;delete
// +kubebuilder:rbac:groups=core,resources=configmaps;services,verbs=get;list;watch;create;update;patch;delete
// +kubebuilder:rbac:groups=core,resources=pods,verbs=get;list;watch
// +kubebuilder:rbac:groups=core,resources=events,verbs=create;patch
// +kubebuilder:rbac:groups=events.k8s.io,resources=events,verbs=create;patch
// +kubebuilder:rbac:groups=batch,resources=jobs,verbs=get;list;watch;create;delete
// +kubebuilder:rbac:groups=batch,resources=cronjobs,verbs=get;list;watch;create;update;patch;delete

// Reconcile drives a FeedListener toward its declared state: one ConfigMap of shared feed
// configuration, one Service fronting the health endpoints, and one Deployment per shard.
func (r *FeedListenerReconciler) Reconcile(ctx context.Context, req ctrl.Request) (ctrl.Result, error) {
	log := logf.FromContext(ctx)

	var fl elitev1alpha1.FeedListener
	if err := r.Get(ctx, req.NamespacedName, &fl); err != nil {
		// NotFound means the object is gone and its finalizer already ran. Nothing to undo:
		// every in-cluster child is owner-referenced and collected by Kubernetes itself.
		return ctrl.Result{}, client.IgnoreNotFound(err)
	}

	if !fl.DeletionTimestamp.IsZero() {
		return r.finalize(ctx, &fl)
	}

	// The finalizer must be in place before any child exists, so a delete that lands mid-create
	// still gets a drain.
	//
	// This used to register the finalizer and return, letting the watch event from its own write
	// carry the reconcile forward. That is no longer safe and was always fragile: adding a
	// finalizer is a *metadata* change, so it does not bump metadata.generation, and
	// feedListenerTriggers now filters exactly that kind of event. The early return would leave a
	// brand-new resource with a finalizer, no children, and nothing scheduled to wake it.
	// Falling through instead makes the first pass self-sufficient — r.Update has already written
	// the new resourceVersion back into fl, so the rest of the reconcile operates on a current
	// object.
	if !controllerutil.ContainsFinalizer(&fl, elitev1alpha1.FeedListenerFinalizer) {
		controllerutil.AddFinalizer(&fl, elitev1alpha1.FeedListenerFinalizer)
		if err := r.Update(ctx, &fl); err != nil {
			return ctrl.Result{}, fmt.Errorf("registering finalizer: %w", err)
		}
	}

	configHash, err := r.reconcileConfigMap(ctx, &fl)
	if err != nil {
		return r.failed(ctx, &fl, "ConfigMapError", err)
	}

	if err := r.reconcileService(ctx, &fl); err != nil {
		return r.failed(ctx, &fl, "ServiceError", err)
	}

	if err := r.reconcileShards(ctx, &fl, configHash); err != nil {
		return r.failed(ctx, &fl, "ShardError", err)
	}

	if err := r.reconcileMaintenance(ctx, &fl); err != nil {
		return r.failed(ctx, &fl, "MaintenanceError", err)
	}

	log.V(1).Info("reconciled feed listener", "consumers", fl.Spec.Consumers, "configHash", configHash)

	return r.reconcileStatus(ctx, &fl)
}

// reconcileConfigMap writes the shared feed configuration and returns its digest, which the pod
// templates carry so a configuration change actually rolls the consumers.
func (r *FeedListenerReconciler) reconcileConfigMap(ctx context.Context, fl *elitev1alpha1.FeedListener) (string, error) {
	desired := BuildConfigMap(fl)

	cm := &corev1.ConfigMap{}
	cm.Name = desired.Name
	cm.Namespace = desired.Namespace

	_, err := controllerutil.CreateOrUpdate(ctx, r.Client, cm, func() error {
		cm.Labels = desired.Labels
		cm.Data = desired.Data
		return controllerutil.SetControllerReference(fl, cm, r.Scheme)
	})
	if err != nil {
		return "", err
	}
	return hashConfigData(desired.Data), nil
}

func (r *FeedListenerReconciler) reconcileService(ctx context.Context, fl *elitev1alpha1.FeedListener) error {
	desired := BuildService(fl)

	svc := &corev1.Service{}
	svc.Name = desired.Name
	svc.Namespace = desired.Namespace

	_, err := controllerutil.CreateOrUpdate(ctx, r.Client, svc, func() error {
		svc.Labels = desired.Labels
		// Only the fields we own are assigned. Replacing the whole ServiceSpec would blank
		// ClusterIP, which the API server has already allocated and treats as immutable.
		svc.Spec.Selector = desired.Spec.Selector
		svc.Spec.Ports = desired.Spec.Ports
		return controllerutil.SetControllerReference(fl, svc, r.Scheme)
	})
	return err
}

// reconcileShards creates or updates one Deployment per shard and removes any shard that falls
// outside the current consumer count.
//
// The pruning half is not optional bookkeeping. Scaling consumers from 4 down to 2 leaves
// shards 2 and 3 running, still subscribed, still writing — and owner-reference garbage
// collection will never touch them, because their owner is alive. Only the controller knows
// they are no longer part of the partition.
func (r *FeedListenerReconciler) reconcileShards(ctx context.Context, fl *elitev1alpha1.FeedListener, configHash string) error {
	for shard := int32(0); shard < fl.Spec.Consumers; shard++ {
		desired := BuildShardDeployment(fl, shard, configHash)

		deploy := &appsv1.Deployment{}
		deploy.Name = desired.Name
		deploy.Namespace = desired.Namespace

		_, err := controllerutil.CreateOrUpdate(ctx, r.Client, deploy, func() error {
			deploy.Labels = desired.Labels
			deploy.Spec.Replicas = desired.Spec.Replicas
			deploy.Spec.Strategy = desired.Spec.Strategy
			// Selector is immutable once set; assigning it on create only avoids a rejected
			// update on every later reconcile.
			if deploy.Spec.Selector == nil {
				deploy.Spec.Selector = desired.Spec.Selector
			}
			deploy.Spec.Template = desired.Spec.Template
			return controllerutil.SetControllerReference(fl, deploy, r.Scheme)
		})
		if err != nil {
			return fmt.Errorf("shard %d: %w", shard, err)
		}
	}

	return r.pruneStaleShards(ctx, fl)
}

// reconcileMaintenance keeps the index-rebuild CronJob in step with spec.indexMaintenance,
// including removing it when the schedule is cleared.
//
// The delete half matters as much as the create half, and for the same reason shard pruning does:
// clearing the schedule hands the work back to the consumers' in-process timer, and a CronJob
// left behind would keep firing alongside it. Two things reconciling one index is not a
// correctness bug — the rebuild is idempotent — but it is a full duplicate scan of the keyspace
// on every tick, which is the exact cost this change exists to remove.
func (r *FeedListenerReconciler) reconcileMaintenance(ctx context.Context, fl *elitev1alpha1.FeedListener) error {
	log := logf.FromContext(ctx)

	if _, scheduled := maintenanceSchedule(fl); !scheduled {
		return r.deleteMaintenanceCronJob(ctx, fl)
	}

	desired := BuildMaintenanceCronJob(fl)

	cronJob := &batchv1.CronJob{}
	cronJob.Name = desired.Name
	cronJob.Namespace = desired.Namespace

	// Unlike the drain Job, a CronJob is safely updatable: it is a template, not a running pod,
	// so a changed image or schedule simply applies to the next tick.
	_, err := controllerutil.CreateOrUpdate(ctx, r.Client, cronJob, func() error {
		cronJob.Labels = desired.Labels
		cronJob.Spec.Schedule = desired.Spec.Schedule
		cronJob.Spec.ConcurrencyPolicy = desired.Spec.ConcurrencyPolicy
		cronJob.Spec.StartingDeadlineSeconds = desired.Spec.StartingDeadlineSeconds
		cronJob.Spec.SuccessfulJobsHistoryLimit = desired.Spec.SuccessfulJobsHistoryLimit
		cronJob.Spec.FailedJobsHistoryLimit = desired.Spec.FailedJobsHistoryLimit
		cronJob.Spec.JobTemplate = desired.Spec.JobTemplate
		return controllerutil.SetControllerReference(fl, cronJob, r.Scheme)
	})
	if err != nil {
		return fmt.Errorf("reconciling maintenance cronjob: %w", err)
	}

	log.V(1).Info("reconciled index maintenance", "schedule", desired.Spec.Schedule)
	return nil
}

func (r *FeedListenerReconciler) deleteMaintenanceCronJob(ctx context.Context, fl *elitev1alpha1.FeedListener) error {
	cronJob := &batchv1.CronJob{}
	cronJob.Name = maintenanceCronJobName(fl)
	cronJob.Namespace = fl.Namespace

	if err := r.Delete(ctx, cronJob); err != nil {
		if apierrors.IsNotFound(err) {
			return nil
		}
		return fmt.Errorf("removing maintenance cronjob: %w", err)
	}
	logf.FromContext(ctx).Info("removed index maintenance cronjob; upkeep returns to shard 0")
	return nil
}

func (r *FeedListenerReconciler) pruneStaleShards(ctx context.Context, fl *elitev1alpha1.FeedListener) error {
	log := logf.FromContext(ctx)

	var owned appsv1.DeploymentList
	if err := r.List(ctx, &owned,
		client.InNamespace(fl.Namespace),
		client.MatchingLabels(selectorLabels(fl)),
	); err != nil {
		return fmt.Errorf("listing shards: %w", err)
	}

	for i := range owned.Items {
		deploy := &owned.Items[i]
		shard, ok := isOwnedShard(fl, deploy)
		if !ok || shard < fl.Spec.Consumers {
			continue
		}
		log.Info("pruning shard outside the partition", "deployment", deploy.Name, "shard", shard)
		if err := r.Delete(ctx, deploy); err != nil && !apierrors.IsNotFound(err) {
			return fmt.Errorf("pruning shard %d: %w", shard, err)
		}
		r.eventf(fl, corev1.EventTypeNormal, "ShardPruned", "Prune",
			"Removed shard %d; consumer count is now %d", shard, fl.Spec.Consumers)
	}
	return nil
}

// failed records a reconcile error on the resource before returning it, so an operator reading
// `kubectl describe` sees why the feed is stuck without going to the controller logs.
func (r *FeedListenerReconciler) failed(ctx context.Context, fl *elitev1alpha1.FeedListener, reason string, err error) (ctrl.Result, error) {
	r.eventf(fl, corev1.EventTypeWarning, reason, "Reconcile", "%v", err)
	if statusErr := r.markDegraded(ctx, fl, reason, err); statusErr != nil {
		logf.FromContext(ctx).Error(statusErr, "recording degraded status")
	}
	return ctrl.Result{}, err
}

// eventf records one event against the FeedListener.
//
// This uses the events.k8s.io/v1 recorder rather than the deprecated core/v1 one, which is why
// the signature carries an action: the new API separates *what the controller was doing* (action,
// a machine-readable verb) from *what happened* (reason). The "related" object is always nil —
// every event here is about the FeedListener itself, not about a second object it acted on.
func (r *FeedListenerReconciler) eventf(fl *elitev1alpha1.FeedListener, eventType, reason, action, messageFmt string, args ...any) {
	if r.Recorder == nil {
		return
	}
	r.Recorder.Eventf(fl, nil, eventType, reason, action, messageFmt, args...)
}

// feedListenerTriggers decides which writes to a FeedListener wake the controller.
//
// Without it, For(&FeedListener{}) wakes on every write to the resource — including the
// controller's own Status().Update at the end of reconcileStatus. That status carries
// LastMessageTime, which advances continuously on a live feed, so every scheduled poll produced a
// second, immediate reconcile and a second status write. Measured in production before this
// predicate: 12 reconciles per 180s against the 6 that statusPollInterval asks for, indefinitely.
//
// Generation is the signal that separates the two cases, because status subresource writes never
// bump metadata.generation while spec edits always do.
//
// Deletion is admitted explicitly rather than leaning on the API server also bumping generation
// when it stamps deletionTimestamp. That behaviour is real, but the drain is the one path where a
// missed event wedges the namespace behind an unsatisfiable finalizer, so it should not rest on a
// detail this far from the code. Create, Delete and Generic events are not filtered at all —
// predicate.Funcs admits anything its fields leave unset.
func feedListenerTriggers() predicate.Predicate {
	return predicate.Funcs{
		UpdateFunc: func(e event.UpdateEvent) bool {
			if e.ObjectOld == nil || e.ObjectNew == nil {
				return true
			}
			if !e.ObjectNew.GetDeletionTimestamp().IsZero() {
				return true
			}
			return e.ObjectOld.GetGeneration() != e.ObjectNew.GetGeneration()
		},
	}
}

// SetupWithManager sets up the controller with the Manager.
func (r *FeedListenerReconciler) SetupWithManager(mgr ctrl.Manager) error {
	if r.StreamProbe == nil {
		r.StreamProbe = NewHTTPStreamProbe()
	}
	if r.Recorder == nil {
		r.Recorder = mgr.GetEventRecorder("feedlistener-controller")
	}

	return ctrl.NewControllerManagedBy(mgr).
		For(&elitev1alpha1.FeedListener{}, builder.WithPredicates(feedListenerTriggers())).
		// Owning the children means a hand-edited or deleted child triggers a reconcile that
		// puts it back, rather than drifting until something notices.
		Owns(&appsv1.Deployment{}).
		Owns(&corev1.ConfigMap{}).
		Owns(&corev1.Service{}).
		Owns(&batchv1.Job{}).
		Owns(&batchv1.CronJob{}).
		Named("feedlistener").
		Complete(r)
}
