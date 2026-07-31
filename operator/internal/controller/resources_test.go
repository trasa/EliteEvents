package controller

import (
	"fmt"
	"testing"
	"time"

	batchv1 "k8s.io/api/batch/v1"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/labels"

	elitev1alpha1 "github.com/trasa/EliteEvents/operator/api/v1alpha1"
)

func testFeedListener(consumers int32) *elitev1alpha1.FeedListener {
	return &elitev1alpha1.FeedListener{
		ObjectMeta: metav1.ObjectMeta{Name: "eddn", Namespace: "elite"},
		Spec: elitev1alpha1.FeedListenerSpec{
			RelayEndpoint:         "tcp://eddn.edcd.io:9500",
			Consumers:             consumers,
			Image:                 "registry.digitalocean.com/meancat/elite-ingestion:1.0.0",
			ReconnectAfterSilence: metav1.Duration{Duration: 2 * time.Minute},
			Redis: elitev1alpha1.RedisConfig{
				ConnectionString: "redis:6379,abortConnect=false",
				AuthSecret:       &elitev1alpha1.RedisAuthSecret{Name: "redis-auth", Key: "password"},
			},
		},
	}
}

// .NET's TimeSpan parser rejects Go's native duration format outright, so a regression here
// would not fail loudly — the consumer would silently fall back to its default threshold.
func TestASPNetDuration(t *testing.T) {
	cases := []struct {
		in   time.Duration
		want string
	}{
		{2 * time.Minute, "00:02:00"},
		{30 * time.Second, "00:00:30"},
		{90 * time.Second, "00:01:30"},
		{time.Hour + 5*time.Minute + 3*time.Second, "01:05:03"},
		{0, "00:00:00"},
		{-time.Minute, "00:00:00"},
	}
	for _, tc := range cases {
		if got := aspNetDuration(tc.in); got != tc.want {
			t.Errorf("aspNetDuration(%s) = %q, want %q", tc.in, got, tc.want)
		}
	}
}

// The digest drives pod rollout. If it were unstable the Deployment would roll on every
// reconcile; if it were insensitive, a config change would never reach running pods.
func TestHashConfigDataIsStableAndSensitive(t *testing.T) {
	fl := testFeedListener(1)

	first := hashConfigData(buildConfigData(fl))
	for i := 0; i < 100; i++ {
		if got := hashConfigData(buildConfigData(fl)); got != first {
			t.Fatalf("hash changed across calls: %q then %q", first, got)
		}
	}

	fl.Spec.RelayEndpoint = "tcp://relay.example.org:9500"
	if got := hashConfigData(buildConfigData(fl)); got == first {
		t.Error("hash did not change when the relay endpoint changed")
	}
}

// Every shard must receive a distinct index, and every index in [0, consumers) must be covered
// exactly once. This is the invariant the whole design exists to protect: a gap silently drops
// a slice of the feed, and an overlap double-counts it.
func TestShardPartitionIsExhaustiveAndDisjoint(t *testing.T) {
	for _, consumers := range []int32{1, 2, 3, 4, 16} {
		fl := testFeedListener(consumers)
		seen := map[string]int{}
		names := map[string]bool{}

		for shard := int32(0); shard < consumers; shard++ {
			deploy := BuildShardDeployment(fl, shard, "hash")

			if names[deploy.Name] {
				t.Fatalf("consumers=%d: duplicate deployment name %q", consumers, deploy.Name)
			}
			names[deploy.Name] = true

			if got := *deploy.Spec.Replicas; got != 1 {
				t.Errorf("consumers=%d shard=%d: replicas = %d, want 1 (a second pod of the same shard double-counts)", consumers, shard, got)
			}

			var index string
			for _, env := range deploy.Spec.Template.Spec.Containers[0].Env {
				if env.Name == "Eddn__ShardIndex" {
					index = env.Value
				}
			}
			if index == "" {
				t.Fatalf("consumers=%d shard=%d: no Eddn__ShardIndex set", consumers, shard)
			}
			seen[index]++
		}

		if len(seen) != int(consumers) {
			t.Errorf("consumers=%d: covered %d distinct shard indexes, want %d", consumers, len(seen), consumers)
		}
		for shard := int32(0); shard < consumers; shard++ {
			key := fmt.Sprint(shard)
			if seen[key] != 1 {
				t.Errorf("consumers=%d: shard index %s assigned %d times, want exactly 1", consumers, key, seen[key])
			}
		}
	}
}

// Every shard must agree on the total, or the hash partition each one computes locally would
// not line up with its neighbours'.
func TestShardCountIsSharedConfiguration(t *testing.T) {
	fl := testFeedListener(4)
	if got := buildConfigData(fl)["Eddn__ShardCount"]; got != "4" {
		t.Errorf("Eddn__ShardCount = %q, want %q", got, "4")
	}
}

// Regression: the drain pod must not match the consumer selector. If it does, stopConsumers
// waits for a pod set that includes the drain pod itself, so the finalizer never completes and
// the resource is stuck in Terminating until someone hand-edits it.
func TestDrainPodIsInvisibleToConsumerSelector(t *testing.T) {
	fl := testFeedListener(2)
	job := BuildDrainJob(fl)

	selector := labels.SelectorFromSet(selectorLabels(fl))
	if selector.Matches(labels.Set(job.Spec.Template.Labels)) {
		t.Fatal("drain pod matches the consumer selector; the finalizer would wait on itself")
	}
	if selector.Matches(labels.Set(job.Labels)) {
		t.Error("drain job matches the consumer selector")
	}
}

// A shard is only prunable when it falls outside the current consumer count. Misreading an
// in-range shard as stale would delete a live writer.
func TestIsOwnedShard(t *testing.T) {
	fl := testFeedListener(2)

	deploy := BuildShardDeployment(fl, 3, "hash")
	shard, ok := isOwnedShard(fl, deploy)
	if !ok || shard != 3 {
		t.Fatalf("isOwnedShard = (%d, %v), want (3, true)", shard, ok)
	}
	if shard < fl.Spec.Consumers {
		t.Error("shard 3 should be outside a 2-consumer partition")
	}

	foreign := BuildShardDeployment(testFeedListener(1), 0, "hash")
	foreign.Labels[instanceLabel] = "another-feed"
	if _, ok := isOwnedShard(fl, foreign); ok {
		t.Error("a deployment from another feed was claimed as an owned shard")
	}

	unlabelled := BuildShardDeployment(fl, 0, "hash")
	delete(unlabelled.Labels, shardLabel)
	if _, ok := isOwnedShard(fl, unlabelled); ok {
		t.Error("a deployment with no shard label was claimed as an owned shard")
	}
}

// The Deployment selector is immutable once created, so it must not contain anything that can
// change with the spec.
func TestSelectorLabelsAreSpecIndependent(t *testing.T) {
	a := testFeedListener(1)
	b := testFeedListener(8)
	b.Spec.RelayEndpoint = "tcp://relay.example.org:9500"
	b.Spec.Image = "different:tag"

	first, second := selectorLabels(a), selectorLabels(b)
	if len(first) != len(second) {
		t.Fatalf("selector labels differ in size: %v vs %v", first, second)
	}
	for k, v := range first {
		if second[k] != v {
			t.Errorf("selector label %q changed with the spec: %q vs %q", k, v, second[k])
		}
	}
}

func TestRedisAuthIsMountedAsAFile(t *testing.T) {
	fl := testFeedListener(1)
	deploy := BuildShardDeployment(fl, 0, "hash")

	mounts := deploy.Spec.Template.Spec.Containers[0].VolumeMounts
	if len(mounts) != 1 || mounts[0].MountPath != redisAuthMountPath || !mounts[0].ReadOnly {
		t.Fatalf("expected a read-only redis-auth mount, got %+v", mounts)
	}
	if got := buildConfigData(fl)["REDIS_AUTH_FILE"]; got != redisAuthMountPath+"/password" {
		t.Errorf("REDIS_AUTH_FILE = %q", got)
	}

	// The password must never reach the connection string: it can contain ',' or '=', which
	// would corrupt the parse.
	for _, env := range deploy.Spec.Template.Spec.Containers[0].Env {
		if env.Name == "ConnectionStrings__Redis" {
			t.Error("connection string was set as a per-shard env var rather than shared config")
		}
	}
}

func TestNoAuthSecretMeansNoVolume(t *testing.T) {
	fl := testFeedListener(1)
	fl.Spec.Redis.AuthSecret = nil

	deploy := BuildShardDeployment(fl, 0, "hash")
	if len(deploy.Spec.Template.Spec.Volumes) != 0 {
		t.Error("expected no volumes when no auth secret is configured")
	}
	if _, ok := buildConfigData(fl)["REDIS_AUTH_FILE"]; ok {
		t.Error("REDIS_AUTH_FILE should be absent when no auth secret is configured")
	}
}

// withMaintenance returns a feed with an index-rebuild schedule. Tests must set this explicitly:
// the empty-object default that normally supplies it is applied by the API server, and objects
// built in-process never go through defaulting.
func withMaintenance(fl *elitev1alpha1.FeedListener, schedule string) *elitev1alpha1.FeedListener {
	fl.Spec.IndexMaintenance = &elitev1alpha1.IndexMaintenanceSpec{
		Schedule:              &schedule,
		ActiveDeadlineSeconds: 900,
	}
	return fl
}

// The rebuild Job must run the ingestion image, because that image is where RedisKeys lives. If
// this ever became a generic image running redis-cli, the keyspace would have a second
// definition — the failure the drain Job is deliberately shaped to avoid.
func TestMaintenanceCronJobRunsTheIngestionImage(t *testing.T) {
	fl := withMaintenance(testFeedListener(2), "17 * * * *")
	cj := BuildMaintenanceCronJob(fl)

	if cj.Spec.Schedule != "17 * * * *" {
		t.Errorf("schedule = %q", cj.Spec.Schedule)
	}
	container := cj.Spec.JobTemplate.Spec.Template.Spec.Containers[0]
	if container.Image != fl.Spec.Image {
		t.Errorf("image = %q, want the consumer image %q", container.Image, fl.Spec.Image)
	}
	if len(container.Args) != 1 || container.Args[0] != "--rebuild-indexes" {
		t.Errorf("args = %v, want [--rebuild-indexes]", container.Args)
	}

	// Same ConfigMap as the consumers: one copy of the Redis connection details, so the rebuild
	// cannot end up reconciling an index in a different Redis than the one being written.
	if len(container.EnvFrom) != 1 || container.EnvFrom[0].ConfigMapRef.Name != configMapName(fl) {
		t.Errorf("envFrom = %+v, want the shared config map", container.EnvFrom)
	}
	if mounts := container.VolumeMounts; len(mounts) != 1 || mounts[0].MountPath != redisAuthMountPath {
		t.Errorf("expected the redis-auth mount, got %+v", mounts)
	}
}

// Overlapping rebuilds are not incorrect, just wasteful in exactly the way this whole change
// exists to stop: two full passes over the same keyspace converging on the same answer.
func TestMaintenanceCronJobForbidsConcurrentRebuilds(t *testing.T) {
	fl := withMaintenance(testFeedListener(1), "17 * * * *")
	cj := BuildMaintenanceCronJob(fl)

	if cj.Spec.ConcurrencyPolicy != batchv1.ForbidConcurrent {
		t.Errorf("concurrencyPolicy = %q, want Forbid", cj.Spec.ConcurrencyPolicy)
	}
	if cj.Spec.JobTemplate.Spec.ActiveDeadlineSeconds == nil {
		t.Error("expected an activeDeadlineSeconds bound on a rebuild")
	}
}

// The three pod populations must stay mutually invisible. stopWriters waits for the consumer and
// maintenance selectors to come back empty before it starts the drain; if the drain pod matched
// either, the finalizer would wait on itself and wedge the resource in Terminating forever.
func TestWriterAndDrainSelectorsAreDisjoint(t *testing.T) {
	fl := withMaintenance(testFeedListener(2), "17 * * * *")

	consumers := labels.SelectorFromSet(selectorLabels(fl))
	maintenance := labels.SelectorFromSet(maintenanceLabels(fl))

	drainPod := labels.Set(BuildDrainJob(fl).Spec.Template.Labels)
	if consumers.Matches(drainPod) {
		t.Error("drain pod matches the consumer selector; the finalizer would wait on itself")
	}
	if maintenance.Matches(drainPod) {
		t.Error("drain pod matches the maintenance selector; the finalizer would wait on itself")
	}

	rebuildPod := labels.Set(BuildMaintenanceCronJob(fl).Spec.JobTemplate.Spec.Template.Labels)
	if consumers.Matches(rebuildPod) {
		t.Error("rebuild pod matches the consumer selector; it would join the Service and be " +
			"counted as a ready consumer despite serving no HTTP")
	}
	if !maintenance.Matches(rebuildPod) {
		t.Error("rebuild pod does not match the maintenance selector; teardown would not wait for it")
	}
}

// The handoff contract. Exactly one thing may rebuild on a schedule: the CronJob when there is
// one, the consumers' own timer when there is not. Both at once is a duplicated full scan on
// every tick; neither is an index that silently stops being reconciled.
func TestScheduleAndInProcessTimerAreMutuallyExclusive(t *testing.T) {
	scheduled := withMaintenance(testFeedListener(2), "17 * * * *")
	if got := buildConfigData(scheduled)["IndexMaintenance__Periodic"]; got != "false" {
		t.Errorf("with a schedule, IndexMaintenance__Periodic = %q, want %q", got, "false")
	}

	unscheduled := withMaintenance(testFeedListener(2), "")
	if got := buildConfigData(unscheduled)["IndexMaintenance__Periodic"]; got != "true" {
		t.Errorf("without a schedule, IndexMaintenance__Periodic = %q, want %q", got, "true")
	}

	// An absent spec is the in-process shape too, so a FeedListener built without defaulting
	// never silently loses index upkeep altogether.
	nilSpec := testFeedListener(2)
	nilSpec.Spec.IndexMaintenance = nil
	if got := buildConfigData(nilSpec)["IndexMaintenance__Periodic"]; got != "true" {
		t.Errorf("with no maintenance spec, IndexMaintenance__Periodic = %q, want %q", got, "true")
	}

	// Adding or removing a schedule must roll the consumers: the flag only takes effect at
	// startup, so a pod that keeps running keeps its old timer.
	if hashConfigData(buildConfigData(scheduled)) == hashConfigData(buildConfigData(unscheduled)) {
		t.Error("config hash is identical with and without a schedule; the pods would not roll " +
			"and would keep the timer they started with")
	}
}
