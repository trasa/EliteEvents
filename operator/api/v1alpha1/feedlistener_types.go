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

	// resources overrides the compute resources of the consumer container.
	// +optional
	Resources corev1.ResourceRequirements `json:"resources,omitzero"`

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
