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
	batchv1 "k8s.io/api/batch/v1"
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

	// probeTimeoutSeconds is set explicitly because the Kubernetes default is 1s, which is far
	// too tight for a readiness check that talks to Redis over a shared multiplexer. Consumers
	// were going NotReady on "context deadline exceeded" whenever a single slow command stalled
	// the server. This is deliberately still well under PeriodSeconds so a probe cannot overlap
	// its own next firing.
	probeTimeoutSeconds = 3

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
func maintenanceCronJobName(fl *elitev1alpha1.FeedListener) string { return fl.Name + "-rebuild" }

// maintenanceSchedule returns the cron expression for index upkeep, and whether there is one.
// A nil spec, a nil schedule and an explicitly empty one all mean "no CronJob" — the distinction
// between the last two matters only to the API server's defaulting; see
// IndexMaintenanceSpec.Schedule.
func maintenanceSchedule(fl *elitev1alpha1.FeedListener) (string, bool) {
	if fl.Spec.IndexMaintenance == nil || fl.Spec.IndexMaintenance.Schedule == nil {
		return "", false
	}
	schedule := *fl.Spec.IndexMaintenance.Schedule
	return schedule, schedule != ""
}

// maintenanceLabels deliberately do not match selectorLabels, for the same reason drainLabels do
// not: the rebuild pod must be invisible both to the consumer Service — it serves no HTTP at all,
// and an endpoint pointing at it would be a black hole — and to the pod waits during teardown,
// which are sequenced separately.
func maintenanceLabels(fl *elitev1alpha1.FeedListener) map[string]string {
	return map[string]string{
		nameLabel:      "feed-maintenance",
		instanceLabel:  fl.Name,
		partOfLabel:    "elite-events",
		componentLabel: "maintenance",
	}
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
	// Exactly one thing may rebuild the indexes on a schedule. This key is the handoff: when a
	// CronJob exists the consumers' own timer is switched off, and when it does not they keep it.
	// It is written either way rather than only when false, so the ConfigMap states which mode
	// the pods are in — and so that adding or removing a schedule changes the config hash and
	// actually rolls them.
	_, scheduled := maintenanceSchedule(fl)

	data := map[string]string{
		"ASPNETCORE_ENVIRONMENT":      "Production",
		"ConnectionStrings__Redis":    fl.Spec.Redis.ConnectionString,
		"Eddn__StreamUrl":             fl.Spec.RelayEndpoint,
		"Eddn__ShardCount":            strconv.Itoa(int(fl.Spec.Consumers)),
		"Eddn__ReconnectAfterSilence": aspNetDuration(fl.Spec.ReconnectAfterSilence.Duration),
		"IndexMaintenance__Periodic":  strconv.FormatBool(!scheduled),
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
			TimeoutSeconds:      probeTimeoutSeconds,
		},
		// Readiness reaches Redis, so its timeout has to allow for a server that is briefly busy.
		// Both timeouts were left unset, which meant the Kubernetes default of 1s: a single slow
		// command on a single-threaded Redis was enough to blow the whole budget and take the
		// only writer out of service. The consumer also runs on a 1-vCPU node with a blocking
		// receive loop holding a thread-pool worker, so a 1s deadline left no room for the pool
		// to inject a thread for Kestrel to answer on.
		ReadinessProbe: &corev1.Probe{
			ProbeHandler: corev1.ProbeHandler{
				HTTPGet: &corev1.HTTPGetAction{
					Path: "/health/ready",
					Port: intstr.FromString("http"),
				},
			},
			InitialDelaySeconds: 10,
			PeriodSeconds:       15,
			TimeoutSeconds:      probeTimeoutSeconds,
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

// oneShotPodSpec renders a pod that runs the ingestion image as a command and exits: the drain
// during teardown, the index rebuild on a schedule.
//
// Both reuse the consumer image and the consumer ConfigMap on purpose. The image carries
// RedisKeys, so the keyspace keeps exactly one definition and the operator never has to name a
// Redis key in Go; the ConfigMap means these connect to precisely the Redis the consumers wrote
// to, with no second copy of the connection details to drift.
func oneShotPodSpec(fl *elitev1alpha1.FeedListener, containerName, arg string) corev1.PodSpec {
	container := corev1.Container{
		Name:  containerName,
		Image: fl.Spec.Image,
		Args:  []string{arg},
		EnvFrom: []corev1.EnvFromSource{{
			ConfigMapRef: &corev1.ConfigMapEnvSource{
				LocalObjectReference: corev1.LocalObjectReference{Name: configMapName(fl)},
			},
		}},
		SecurityContext: &corev1.SecurityContext{
			AllowPrivilegeEscalation: ptr(false),
			Capabilities:             &corev1.Capabilities{Drop: []corev1.Capability{"ALL"}},
		},
	}

	spec := corev1.PodSpec{
		RestartPolicy:    corev1.RestartPolicyNever,
		ImagePullSecrets: fl.Spec.ImagePullSecrets,
		SecurityContext:  &corev1.PodSecurityContext{RunAsNonRoot: ptr(true)},
		Containers:       []corev1.Container{container},
	}

	if fl.Spec.Redis.AuthSecret != nil {
		spec.Volumes = []corev1.Volume{{
			Name: redisAuthVolume,
			VolumeSource: corev1.VolumeSource{
				Secret: &corev1.SecretVolumeSource{SecretName: fl.Spec.Redis.AuthSecret.Name},
			},
		}}
		spec.Containers[0].VolumeMounts = []corev1.VolumeMount{{
			Name:      redisAuthVolume,
			MountPath: redisAuthMountPath,
			ReadOnly:  true,
		}}
	}

	return spec
}

// BuildMaintenanceCronJob renders the scheduled index rebuild.
//
// This exists because the rebuild is not shardable. Ingestion is partitioned by message hash, but
// a rebuild reconciles the entire index against the entire keyspace — so running it inside the
// consumers means every shard performing the same full pass to reach the same result. One
// CronJob does it once, and the history it leaves behind is a record of upkeep that a timer
// inside a long-lived process does not produce.
//
// ConcurrencyPolicy is Forbid for the same reason: two overlapping passes duplicate a scan of the
// whole keyspace to converge on the identical answer.
func BuildMaintenanceCronJob(fl *elitev1alpha1.FeedListener) *batchv1.CronJob {
	schedule, _ := maintenanceSchedule(fl)
	labels := maintenanceLabels(fl)

	var activeDeadline *int64
	if fl.Spec.IndexMaintenance != nil && fl.Spec.IndexMaintenance.ActiveDeadlineSeconds > 0 {
		activeDeadline = ptr(fl.Spec.IndexMaintenance.ActiveDeadlineSeconds)
	}

	return &batchv1.CronJob{
		ObjectMeta: metav1.ObjectMeta{
			Name:      maintenanceCronJobName(fl),
			Namespace: fl.Namespace,
			Labels:    labels,
		},
		Spec: batchv1.CronJobSpec{
			Schedule:          schedule,
			ConcurrencyPolicy: batchv1.ForbidConcurrent,
			// A missed tick is not worth catching up on. The next one does the same full
			// reconcile, and a burst of backfilled runs after a control-plane outage would all
			// scan the same keyspace to the same end.
			StartingDeadlineSeconds:    ptr(int64(60)),
			SuccessfulJobsHistoryLimit: ptr(int32(3)),
			FailedJobsHistoryLimit:     ptr(int32(3)),
			JobTemplate: batchv1.JobTemplateSpec{
				ObjectMeta: metav1.ObjectMeta{Labels: labels},
				Spec: batchv1.JobSpec{
					BackoffLimit:          ptr(int32(2)),
					ActiveDeadlineSeconds: activeDeadline,
					Template: corev1.PodTemplateSpec{
						ObjectMeta: metav1.ObjectMeta{Labels: labels},
						Spec:       oneShotPodSpec(fl, "rebuild", "--rebuild-indexes"),
					},
				},
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
