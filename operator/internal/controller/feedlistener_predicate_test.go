package controller

import (
	"testing"
	"time"

	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"sigs.k8s.io/controller-runtime/pkg/client"
	"sigs.k8s.io/controller-runtime/pkg/event"

	elitev1alpha1 "github.com/trasa/EliteEvents/operator/api/v1alpha1"
)

// The predicate is what keeps reconcileStatus from feeding itself. These tests pin the three
// cases that matter; the middle one is the bug that was in production, and the last one is the
// one whose absence would be far worse than the bug.

// Takes client.Object rather than *FeedListener so that passing nil produces the untyped nil
// interface an absent object actually has on event.UpdateEvent. Typing the parameters would make
// nil a non-nil interface holding a nil pointer, which is a thing controller-runtime never hands
// the predicate — and a test that constructs one only proves the guard cannot defend against it.
func updateEvent(old, updated client.Object) event.UpdateEvent {
	return event.UpdateEvent{ObjectOld: old, ObjectNew: updated}
}

func feedListenerAtGeneration(generation int64) *elitev1alpha1.FeedListener {
	return &elitev1alpha1.FeedListener{
		ObjectMeta: metav1.ObjectMeta{
			Name:       "eddn",
			Namespace:  "elite",
			Generation: generation,
		},
	}
}

// A status write must not wake the controller. reconcileStatus ends every pass with
// Status().Update, and LastMessageTime advances on every one of them because the feed is live —
// so admitting these events makes the controller trigger itself forever.
func TestStatusWriteDoesNotTriggerReconcile(t *testing.T) {
	before := feedListenerAtGeneration(4)
	after := feedListenerAtGeneration(4)
	after.Status.LastMessageTime = &metav1.Time{Time: time.Now()}
	after.Status.ReadyConsumers = 1
	after.Status.Phase = "Streaming"

	if feedListenerTriggers().Update(updateEvent(before, after)) {
		t.Fatal("a status-only update must not trigger a reconcile; " +
			"reconcileStatus writes one on every pass, so admitting it is a self-sustaining loop")
	}
}

// Adding a finalizer is a metadata change and does not bump generation either. Reconcile no
// longer depends on this event — it falls through and creates the children in the same pass —
// and this test records that the predicate genuinely does drop it, so the fall-through is
// load-bearing rather than incidental.
func TestFinalizerRegistrationDoesNotTriggerReconcile(t *testing.T) {
	before := feedListenerAtGeneration(1)
	after := feedListenerAtGeneration(1)
	after.Finalizers = []string{elitev1alpha1.FeedListenerFinalizer}

	if feedListenerTriggers().Update(updateEvent(before, after)) {
		t.Fatal("adding a finalizer does not bump generation, so this event is expected to be " +
			"filtered; Reconcile must not rely on it to continue")
	}
}

func TestSpecChangeTriggersReconcile(t *testing.T) {
	before := feedListenerAtGeneration(4)
	after := feedListenerAtGeneration(5)
	after.Spec.Consumers = 3

	if !feedListenerTriggers().Update(updateEvent(before, after)) {
		t.Fatal("a spec edit bumps generation and must trigger a reconcile")
	}
}

// The drain is the one path where dropping an event does lasting damage: an unsatisfied
// finalizer wedges the whole namespace. The API server does bump generation when it stamps
// deletionTimestamp, so this case would survive a plain GenerationChangedPredicate — but only by
// coincidence. This asserts the predicate admits deletion on its own terms.
func TestDeletionTriggersReconcileWithoutAGenerationBump(t *testing.T) {
	now := metav1.Now()
	before := feedListenerAtGeneration(4)
	after := feedListenerAtGeneration(4)
	after.DeletionTimestamp = &now
	after.Finalizers = []string{elitev1alpha1.FeedListenerFinalizer}

	if !feedListenerTriggers().Update(updateEvent(before, after)) {
		t.Fatal("deletion must trigger a reconcile even with generation unchanged; " +
			"missing it leaves the finalizer unsatisfied and the namespace stuck terminating")
	}
}

// predicate.Funcs admits anything it does not filter. Deleting the resource outright and
// creating a new one both have to reach the controller.
func TestCreateAndDeleteEventsAreNotFiltered(t *testing.T) {
	p := feedListenerTriggers()

	if !p.Create(event.CreateEvent{Object: feedListenerAtGeneration(1)}) {
		t.Error("create events must reach the controller")
	}
	if !p.Delete(event.DeleteEvent{Object: feedListenerAtGeneration(1)}) {
		t.Error("delete events must reach the controller")
	}
	if !p.Generic(event.GenericEvent{Object: feedListenerAtGeneration(1)}) {
		t.Error("generic events must reach the controller")
	}
}

// A nil side means the event carries no comparison to make. Filtering on a missing object would
// silently drop work, so the predicate errs toward reconciling.
func TestMalformedUpdateEventTriggersReconcile(t *testing.T) {
	p := feedListenerTriggers()

	if !p.Update(updateEvent(nil, feedListenerAtGeneration(1))) {
		t.Error("an update with no old object must not be filtered")
	}
	if !p.Update(updateEvent(feedListenerAtGeneration(1), nil)) {
		t.Error("an update with no new object must not be filtered")
	}
}
