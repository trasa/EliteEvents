package controller

import (
	"os"
	"strings"
	"testing"

	corev1 "k8s.io/api/core/v1"
	"k8s.io/client-go/tools/events"
)

// eventf takes four strings in a row — eventType, reason, action, then the note format — because
// that is the shape of the events.k8s.io/v1 recorder. Transposing any adjacent pair still
// compiles and still records an event, just a nonsensical one, and nothing else in the suite
// would notice. FakeRecorder renders "eventtype reason note" and drops the action, so a swap
// between the action and the note format surfaces here as a corrupted note.
func TestEventfArgumentOrder(t *testing.T) {
	recorder := events.NewFakeRecorder(4)
	r := &FeedListenerReconciler{Recorder: recorder}

	r.eventf(feedListenerAtGeneration(1), corev1.EventTypeWarning, "ShardError", "Reconcile",
		"shard %d failed", 3)

	select {
	case got := <-recorder.Events:
		if want := "Warning ShardError shard 3 failed"; got != want {
			t.Fatalf("recorded %q, want %q", got, want)
		}
	default:
		t.Fatal("no event recorded")
	}
}

// Several tests construct the reconciler without a Recorder; SetupWithManager is what fills it
// in. Emitting before that must not panic — an event is diagnostic, and losing one is never
// worth taking the controller down for.
func TestEventfWithoutARecorderIsANoOp(t *testing.T) {
	r := &FeedListenerReconciler{}
	r.eventf(feedListenerAtGeneration(1), corev1.EventTypeNormal, "Drained", "Drain", "purged")
}

// The RBAC half of the same migration, and the half that fails silently.
//
// The events.k8s.io recorder writes Events in the events.k8s.io group, not core/v1. Granting only
// the core group leaves the controller unable to record anything, and nothing surfaces that: no
// probe fails, no reconcile errors, the events simply never appear. This asserts on the generated
// role rather than the kubebuilder markers because the role is what actually gets applied, so it
// catches both a deleted marker and a `make manifests` that was never run.
func TestGeneratedRoleGrantsEventsInBothAPIGroups(t *testing.T) {
	role, err := os.ReadFile("../../config/rbac/role.yaml")
	if err != nil {
		t.Fatalf("reading generated role: %v", err)
	}
	if !strings.Contains(string(role), "events.k8s.io") {
		t.Error("generated role does not grant events.k8s.io; " +
			"the recorder writes there and would fail silently")
	}
}
