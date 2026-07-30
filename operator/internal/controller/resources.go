package controller

import (
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"sort"
	"strconv"
	"strings"
	"time"

	appsv1 "k8s.io/api/apps/v1"
	corev1 "k8s.io/api/core/v1"
	"k8s.io/apimachinery/pkg/api/resource"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/util/intstr"

	elitev1alpha1 "github.com/trasa/EliteEvents/operator/api/v1alpha1"
)

const (
	// containerPort matches the port the ingestion host listens on. It serves nothing but the
	// health endpoints.
	containerPort = 8080

	// redisAuthMountPath is where the password Secret is mounted. The application reads the
	// file named by REDIS_AUTH_FILE and applies it to the parsed ConfigurationOptions.
	redisAuthMountPath = "/etc/redis-auth"
	redisAuthVolume    = "redis-auth"

	// configHashAnnotation carries a digest of the ConfigMap contents on the pod template.
	// Without it a ConfigMap edit would leave running pods on stale configuration: Kubernetes
	// does not restart pods when a ConfigMap they consume changes.
	configHashAnnotation = "elite.meancat.com/config-hash"

	// shardLabel records which shard of the partition a Deployment owns.
	shardLabel = "elite.meancat.com/shard"

	nameLabel      = "app.kubernetes.io/name"
	instanceLabel  = "app.kubernetes.io/instance"
	partOfLabel    = "app.kubernetes.io/part-of"
	componentLabel = "app.kubernetes.io/component"
)

// configMapName, serviceName and shardDeploymentName derive child names from the FeedListener.
// Deriving rather than storing them means a partially-created FeedListener always converges:
// the controller can always recompute what should exist without consulting status.
func configMapName(fl *elitev1alpha1.FeedListener) string { return fl.Name + "-config" }
func serviceName(fl *elitev1alpha1.FeedListener) string   { return fl.Name }
func shardDeploymentName(fl *elitev1alpha1.FeedListener, shard int32) string {
	return fmt.Sprintf("%s-%d", fl.Name, shard)
}

// selectorLabels identify every pod belonging to a FeedListener. They must never include
// anything derived from the spec: a Deployment's selector is immutable, so a label that changed
// with the spec would make the Deployment un-updatable.
func selectorLabels(fl *elitev1alpha1.FeedListener) map[string]string {
	return map[string]string{
		nameLabel:     "feed-listener",
		instanceLabel: fl.Name,
	}
}

// shardSelectorLabels narrow the selector to a single shard's pods.
func shardSelectorLabels(fl *elitev1alpha1.FeedListener, shard int32) map[string]string {
	labels := selectorLabels(fl)
	labels[shardLabel] = strconv.Itoa(int(shard))
	return labels
}

func objectLabels(fl *elitev1alpha1.FeedListener) map[string]string {
	labels := selectorLabels(fl)
	labels[partOfLabel] = "elite-events"
	labels[componentLabel] = "ingestion"
	return labels
}

// aspNetDuration renders a duration the way .NET's TimeSpan parser expects it.
//
// This is a genuine impedance mismatch and not a stylistic choice: Go's Duration.String()
// produces "2m0s", which TimeSpan.Parse rejects outright. Options binding would fall back to
// the default and the reconnect threshold in spec would be silently ignored.
func aspNetDuration(d time.Duration) string {
	if d < 0 {
		d = 0
	}
	totalSeconds := int64(d / time.Second)
	return fmt.Sprintf("%02d:%02d:%02d", totalSeconds/3600, (totalSeconds%3600)/60, totalSeconds%60)
}

// buildConfigData returns the environment shared by every shard of a feed. Shard *index* is
// deliberately absent — it is the one value that differs per Deployment, and putting it here
// would mean one ConfigMap per shard for a single differing key.
func buildConfigData(fl *elitev1alpha1.FeedListener) map[string]string {
	data := map[string]string{
		"ASPNETCORE_ENVIRONMENT":      "Production",
		"ConnectionStrings__Redis":    fl.Spec.Redis.ConnectionString,
		"Eddn__StreamUrl":             fl.Spec.RelayEndpoint,
		"Eddn__ShardCount":            strconv.Itoa(int(fl.Spec.Consumers)),
		"Eddn__ReconnectAfterSilence": aspNetDuration(fl.Spec.ReconnectAfterSilence.Duration),
	}
	if fl.Spec.Redis.AuthSecret != nil {
		data["REDIS_AUTH_FILE"] = redisAuthMountPath + "/" + fl.Spec.Redis.AuthSecret.Key
	}
	return data
}

// hashConfigData digests ConfigMap contents so a change can roll the pods. Keys are sorted
// because Go map iteration order is randomised — an unsorted digest would differ on every
// reconcile and roll the Deployment continuously.
func hashConfigData(data map[string]string) string {
	keys := make([]string, 0, len(data))
	for k := range data {
		keys = append(keys, k)
	}
	sort.Strings(keys)

	h := sha256.New()
	for _, k := range keys {
		h.Write([]byte(k))
		h.Write([]byte{0})
		h.Write([]byte(data[k]))
		h.Write([]byte{0})
	}
	return hex.EncodeToString(h.Sum(nil))[:16]
}

// BuildConfigMap renders the shared configuration for a feed.
func BuildConfigMap(fl *elitev1alpha1.FeedListener) *corev1.ConfigMap {
	return &corev1.ConfigMap{
		ObjectMeta: metav1.ObjectMeta{
			Name:      configMapName(fl),
			Namespace: fl.Namespace,
			Labels:    objectLabels(fl),
		},
		Data: buildConfigData(fl),
	}
}

// BuildService fronts the health endpoints of every shard. Status polling does not use it —
// a load-balanced ClusterIP would answer from an arbitrary shard, and the controller needs to
// ask each pod individually — but it gives uptime monitors and humans one address to hit.
func BuildService(fl *elitev1alpha1.FeedListener) *corev1.Service {
	return &corev1.Service{
		ObjectMeta: metav1.ObjectMeta{
			Name:      serviceName(fl),
			Namespace: fl.Namespace,
			Labels:    objectLabels(fl),
		},
		Spec: corev1.ServiceSpec{
			Selector: selectorLabels(fl),
			Ports: []corev1.ServicePort{{
				Name:       "http",
				Port:       containerPort,
				TargetPort: intstr.FromString("http"),
				Protocol:   corev1.ProtocolTCP,
			}},
		},
	}
}

// defaultResources mirrors what the static ingestion Deployment requested. A CPU limit is
// deliberately omitted: throttling a decompression loop would starve the receive path and
// manufacture the silence the reconnect logic exists to recover from.
func defaultResources() corev1.ResourceRequirements {
	return corev1.ResourceRequirements{
		Requests: corev1.ResourceList{
			corev1.ResourceCPU:    resource.MustParse("100m"),
			corev1.ResourceMemory: resource.MustParse("128Mi"),
		},
		Limits: corev1.ResourceList{
			corev1.ResourceMemory: resource.MustParse("384Mi"),
		},
	}
}

// BuildShardDeployment renders one shard of a feed.
//
// Each shard is its own Deployment with replicas: 1 rather than one Deployment with N replicas,
// because the pods are not interchangeable: each needs a distinct shard index, and a Deployment
// gives every replica an identical pod spec. Recreate is what preserves the invariant that
// matters most — a rollout must never run two pods of the same shard at once, or the overlap
// double-counts every event the way N naive replicas would.
func BuildShardDeployment(fl *elitev1alpha1.FeedListener, shard int32, configHash string) *appsv1.Deployment {
	labels := objectLabels(fl)
	labels[shardLabel] = strconv.Itoa(int(shard))

	resources := fl.Spec.Resources
	if resources.Requests == nil && resources.Limits == nil {
		resources = defaultResources()
	}

	container := corev1.Container{
		Name:  "consumer",
		Image: fl.Spec.Image,
		Ports: []corev1.ContainerPort{{
			Name:          "http",
			ContainerPort: containerPort,
		}},
		EnvFrom: []corev1.EnvFromSource{{
			ConfigMapRef: &corev1.ConfigMapEnvSource{
				LocalObjectReference: corev1.LocalObjectReference{Name: configMapName(fl)},
			},
		}},
		Env: []corev1.EnvVar{{
			Name:  "Eddn__ShardIndex",
			Value: strconv.Itoa(int(shard)),
		}},
		// Liveness runs no checks at all: answering proves the process is up. A quiet relay or
		// an unreachable Redis must not restart a consumer that is already retrying — that is
		// readiness' job to report, and the controller's job to surface as a condition.
		LivenessProbe: &corev1.Probe{
			ProbeHandler: corev1.ProbeHandler{
				HTTPGet: &corev1.HTTPGetAction{
					Path: "/health/live",
					Port: intstr.FromString("http"),
				},
			},
			InitialDelaySeconds: 10,
			PeriodSeconds:       30,
			FailureThreshold:    5,
		},
		ReadinessProbe: &corev1.Probe{
			ProbeHandler: corev1.ProbeHandler{
				HTTPGet: &corev1.HTTPGetAction{
					Path: "/health/ready",
					Port: intstr.FromString("http"),
				},
			},
			InitialDelaySeconds: 10,
			PeriodSeconds:       15,
		},
		Resources: resources,
		SecurityContext: &corev1.SecurityContext{
			AllowPrivilegeEscalation: ptr(false),
			Capabilities:             &corev1.Capabilities{Drop: []corev1.Capability{"ALL"}},
		},
	}

	podSpec := corev1.PodSpec{
		ImagePullSecrets: fl.Spec.ImagePullSecrets,
		SecurityContext:  &corev1.PodSecurityContext{RunAsNonRoot: ptr(true)},
		Containers:       []corev1.Container{container},
	}

	if fl.Spec.Redis.AuthSecret != nil {
		podSpec.Volumes = []corev1.Volume{{
			Name: redisAuthVolume,
			VolumeSource: corev1.VolumeSource{
				Secret: &corev1.SecretVolumeSource{SecretName: fl.Spec.Redis.AuthSecret.Name},
			},
		}}
		podSpec.Containers[0].VolumeMounts = []corev1.VolumeMount{{
			Name:      redisAuthVolume,
			MountPath: redisAuthMountPath,
			ReadOnly:  true,
		}}
	}

	return &appsv1.Deployment{
		ObjectMeta: metav1.ObjectMeta{
			Name:      shardDeploymentName(fl, shard),
			Namespace: fl.Namespace,
			Labels:    labels,
		},
		Spec: appsv1.DeploymentSpec{
			Replicas: ptr(int32(1)),
			Strategy: appsv1.DeploymentStrategy{Type: appsv1.RecreateDeploymentStrategyType},
			Selector: &metav1.LabelSelector{MatchLabels: shardSelectorLabels(fl, shard)},
			Template: corev1.PodTemplateSpec{
				ObjectMeta: metav1.ObjectMeta{
					Labels:      labels,
					Annotations: map[string]string{configHashAnnotation: configHash},
				},
				Spec: podSpec,
			},
		},
	}
}

// isOwnedShard reports whether a Deployment is one of this FeedListener's shards, and which.
// Used to find shards left behind when spec.consumers shrinks: those Deployments are still
// owned, still running, and still writing — nothing garbage-collects them, because from
// Kubernetes' point of view their owner is alive and well.
func isOwnedShard(fl *elitev1alpha1.FeedListener, deploy *appsv1.Deployment) (int32, bool) {
	if deploy.Labels[instanceLabel] != fl.Name || deploy.Labels[nameLabel] != "feed-listener" {
		return 0, false
	}
	raw, ok := deploy.Labels[shardLabel]
	if !ok {
		return 0, false
	}
	shard, err := strconv.Atoi(strings.TrimSpace(raw))
	if err != nil {
		return 0, false
	}
	return int32(shard), true
}

func ptr[T any](v T) *T { return &v }
