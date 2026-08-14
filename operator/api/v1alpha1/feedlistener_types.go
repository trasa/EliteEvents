package v1alpha1

import (
	corev1 "k8s.io/api/core/v1"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/runtime"
)

// Condition types reported on a FeedListener. Available and Progressing follow the usual
// Deployment-shaped meaning; Streaming is specific to this domain and is the reason the CRD
// carries status at all — a listener whose pods are all Ready can still be receiving nothing,
// and that distinction is invisible to a plain Deployment.
const (
	// ConditionAvailable is True when at least one consumer pod is Ready.
	ConditionAvailable = "Available"

	// ConditionProgressing is True while the Deployment is still converging on the desired
	// consumer count.
	ConditionProgressing = "Progressing"

	// ConditionStreaming is True when the feed has delivered a message more recently than
	// ReconnectAfterSilence. This is the health of the external subscription itself, which is
	// what the resource actually models.
	ConditionStreaming = "Streaming"

	// ConditionDegraded is True when reconciliation failed or a child resource is unhealthy.
	ConditionDegraded = "Degraded"
)

// FeedListenerFinalizer guards the Redis state a FeedListener leaves behind. The search
// indexes (index:systems, index:carriers) deliberately carry no TTL and are reconciled only by
// the listener that writes them, so deleting the resource without draining them would orphan
// keys that nothing else will ever reclaim. Owner references cannot express that: the state
// lives in Redis, outside the Kubernetes object graph.
const FeedListenerFinalizer = "elite.meancat.com/drain-feed"

// RedisAuthSecret points at a Secret key holding the Redis password. It is mounted as a file
// rather than injected as an environment variable because the application reads REDIS_AUTH_FILE
// and applies the value to the parsed ConfigurationOptions — a password containing ',' or '='
// would corrupt a connection string if it were concatenated into one.
type RedisAuthSecret struct {
	// name of the Secret in the same namespace as the FeedListener.
	// +required
	// +kubebuilder:validation:MinLength=1
	Name string `json:"name"`

	// key within the Secret holding the password.
	// +optional
	// +kubebuilder:default="password"
	Key string `json:"key,omitempty"`
}

// RedisConfig describes how consumers reach the Redis they write to.
type RedisConfig struct {
	// connectionString is the StackExchange.Redis configuration string, without the password.
	// +required
	// +kubebuilder:validation:MinLength=1
	// +kubebuilder:example="redis:6379,abortConnect=false"
	ConnectionString string `json:"connectionString"`

	// authSecret supplies the password as a mounted file.
	// +optional
	AuthSecret *RedisAuthSecret `json:"authSecret,omitempty"`
}

// IndexMaintenanceSpec schedules the periodic rebuild of this feed's Redis search indexes.
//
// The rebuild belongs to the feed rather than to a resource of its own: index:systems and
// index:carriers are written by these consumers, reconciled against the keys these consumers
// produce, and purged by this resource's finalizer. Splitting upkeep into a second CRD would put
// two controllers in charge of one keyspace with nothing sequencing them.
//
// Unlike the 30-day data, these indexes cannot expire themselves — ZRANGEBYLEX requires every
// member at score 0, so they cannot be scored by age, and a TTL would drop the whole key. A
// periodic rebuild against the live data is what keeps them honest.
type IndexMaintenanceSpec struct {
	// schedule is a cron expression for the rebuild Job, interpreted in the cluster's timezone.
	//
	// An empty schedule creates no CronJob and hands the work back to a timer inside consumer
	// shard 0. That is the local-development shape and the fallback; it is not the default,
	// because in-process upkeep ties a full keyspace pass to the lifetime of a pod whose actual
	// job is to never block on anything.
	//
	// The schedule is validated by the API server when the CronJob is created — a malformed
	// expression surfaces as a Degraded condition and a MaintenanceError event, not as a silently
	// skipped rebuild.
	//
	// It is a pointer so that "unset" and "explicitly empty" stay distinguishable. With a plain
	// string, omitempty drops "" from the serialized object, the API server sees an absent field
	// and re-applies the default — making the schedule impossible to turn off.
	// +optional
	// +kubebuilder:default="17 * * * *"
	// +kubebuilder:validation:MaxLength=200
	Schedule *string `json:"schedule,omitempty"`

	// activeDeadlineSeconds bounds a single rebuild. A pass that has run this long is not going
	// to finish usefully before the next tick, and leaving it running would have two full scans
	// of the same keyspace overlap.
	// +optional
	// +kubebuilder:default=900
	// +kubebuilder:validation:Minimum=60
	ActiveDeadlineSeconds int64 `json:"activeDeadlineSeconds,omitempty"`
}

// FeedListenerSpec describes a subscription to an EDDN relay and the consumers that service it.
type FeedListenerSpec struct {
	// relayEndpoint is the ZeroMQ endpoint to subscribe to.
	// +required
	// +kubebuilder:default="tcp://eddn.edcd.io:9500"
	// +kubebuilder:validation:Pattern=`^tcp://[^/\s]+:[0-9]+$`
	RelayEndpoint string `json:"relayEndpoint"`

	// consumers is the number of pods sharing this subscription.
	//
	// EDDN is a broadcast firehose with no topic frame, so every subscriber receives every
	// message and N naive replicas would count every event N times. Consumers are therefore
	// shards, not replicas: the controller assigns each pod a distinct shard index out of this
	// total, and each pod discards messages that do not hash to its own shard. Keeping that
	// partition exhaustive and non-overlapping is the controller's core invariant.
	// +optional
	// +kubebuilder:default=1
	// +kubebuilder:validation:Minimum=1
	// +kubebuilder:validation:Maximum=16
	Consumers int32 `json:"consumers,omitempty"`

	// image is the consumer container image, including tag.
	// +required
	// +kubebuilder:validation:MinLength=1
	Image string `json:"image"`

	// imagePullSecrets are passed through to the consumer pods. DOKS names its registry pull
	// secret after the registry itself, so this is typically "meancat".
	// +optional
	ImagePullSecrets []corev1.LocalObjectReference `json:"imagePullSecrets,omitempty"`

	// redis is where consumers write what they ingest.
	// +required
	Redis RedisConfig `json:"redis"`

	// reconnectAfterSilence is how long a consumer tolerates silence before rebuilding its
	// socket. It doubles as the threshold for the Streaming condition, so recovery and the
	// status that reports it stay in step.
	// +optional
	// +kubebuilder:default="2m"
	ReconnectAfterSilence metav1.Duration `json:"reconnectAfterSilence,omitempty"`

	// redisUnreachableRestartAfter is how long Redis may be *continuously* unreachable before a
	// consumer's liveness probe fails and Kubernetes restarts it. Zero disables the watchdog.
	//
	// This exists because "the process is running" and "the process still works" came apart: a
	// wedged StackExchange.Redis multiplexer holds sockets it never uses and never replaces, and
	// no amount of waiting fixes it. It took the web tier down for hours on 2026-08-14 while
	// every pod reported Running with zero restarts. A consumer fails quieter still — it serves
	// nothing, so nobody gets a 503; it just stops writing.
	//
	// The default is deliberately far longer than any reconnect a healthy client performs, so
	// that everything below it is still treated as a pod that is merely retrying. This is a spec
	// field rather than a constant so it can be widened, or zeroed, during an incident without
	// rebuilding an image.
	// +optional
	// +kubebuilder:default="15m"
	RedisUnreachableRestartAfter metav1.Duration `json:"redisUnreachableRestartAfter,omitempty"`

	// resources overrides the compute resources of the consumer container.
	// +optional
	Resources corev1.ResourceRequirements `json:"resources,omitzero"`

	// indexMaintenance schedules rebuilds of this feed's Redis search indexes.
	//
	// The empty-object default is what makes the CronJob the normal path: the API server fills
	// this in when it is omitted, and the nested schedule default then applies. A nil pointer
	// only occurs for objects built in-process without defaulting, and means no CronJob.
	// +optional
	// +kubebuilder:default={}
	IndexMaintenance *IndexMaintenanceSpec `json:"indexMaintenance,omitempty"`

	// retainIndexesOnDelete skips the drain Job that would otherwise purge this feed's
	// Redis search indexes when the resource is deleted. Set it when another listener writes
	// the same keyspace, or when the data should outlive the resource that produced it.
	// +optional
	RetainIndexesOnDelete bool `json:"retainIndexesOnDelete,omitempty"`
}

// FeedListenerStatus is the observed state of a FeedListener.
type FeedListenerStatus struct {
	// observedGeneration is the .metadata.generation this status was computed from.
	// +optional
	ObservedGeneration int64 `json:"observedGeneration,omitempty"`

	// phase is a coarse, human-facing summary. Conditions are the machine-readable truth.
	// +optional
	// +kubebuilder:validation:Enum=Pending;Progressing;Streaming;Silent;Degraded;Terminating
	Phase string `json:"phase,omitempty"`

	// readyConsumers is the number of consumer pods currently Ready.
	// +optional
	ReadyConsumers int32 `json:"readyConsumers,omitempty"`

	// desiredConsumers mirrors spec.consumers at the time status was written.
	// +optional
	DesiredConsumers int32 `json:"desiredConsumers,omitempty"`

	// lastMessageTime is the most recent message timestamp reported by any consumer. This is
	// the signal that distinguishes a healthy listener from one that is merely running.
	// +optional
	LastMessageTime *metav1.Time `json:"lastMessageTime,omitempty"`

	// lastIndexRebuildTime is when the maintenance CronJob last completed successfully. Empty
	// while a schedule exists but has not yet fired, and while upkeep runs in-process — the
	// controller reports only what it can observe from the Job it owns.
	// +optional
	LastIndexRebuildTime *metav1.Time `json:"lastIndexRebuildTime,omitempty"`

	// conditions represent the current state of the FeedListener.
	// +listType=map
	// +listMapKey=type
	// +optional
	Conditions []metav1.Condition `json:"conditions,omitempty"`
}

// +kubebuilder:object:root=true
// +kubebuilder:subresource:status
// +kubebuilder:subresource:scale:specpath=.spec.consumers,statuspath=.status.readyConsumers
// +kubebuilder:resource:shortName=feed;feeds
// +kubebuilder:printcolumn:name="Relay",type=string,JSONPath=`.spec.relayEndpoint`
// +kubebuilder:printcolumn:name="Consumers",type=string,JSONPath=`.status.readyConsumers`
// +kubebuilder:printcolumn:name="Phase",type=string,JSONPath=`.status.phase`
// +kubebuilder:printcolumn:name="Last Message",type=date,JSONPath=`.status.lastMessageTime`
// +kubebuilder:printcolumn:name="Age",type=date,JSONPath=`.metadata.creationTimestamp`

// FeedListener is a subscription to an EDDN relay, reconciled into the consumers that service it.
type FeedListener struct {
	metav1.TypeMeta `json:",inline"`

	// metadata is a standard object metadata
	// +optional
	metav1.ObjectMeta `json:"metadata,omitzero"`

	// spec defines the desired state of FeedListener
	// +required
	Spec FeedListenerSpec `json:"spec"`

	// status defines the observed state of FeedListener
	// +optional
	Status FeedListenerStatus `json:"status,omitzero"`
}

// +kubebuilder:object:root=true

// FeedListenerList contains a list of FeedListener
type FeedListenerList struct {
	metav1.TypeMeta `json:",inline"`
	metav1.ListMeta `json:"metadata,omitzero"`
	Items           []FeedListener `json:"items"`
}

func init() {
	SchemeBuilder.Register(func(s *runtime.Scheme) error {
		s.AddKnownTypes(SchemeGroupVersion, &FeedListener{}, &FeedListenerList{})
		return nil
	})
}
