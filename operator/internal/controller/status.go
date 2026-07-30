package controller

import (
	"context"
	"encoding/json"
	"fmt"
	"net"
	"net/http"
	"strconv"
	"time"

	corev1 "k8s.io/api/core/v1"
	"k8s.io/apimachinery/pkg/api/meta"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	ctrl "sigs.k8s.io/controller-runtime"
	"sigs.k8s.io/controller-runtime/pkg/client"
	logf "sigs.k8s.io/controller-runtime/pkg/log"

	elitev1alpha1 "github.com/trasa/EliteEvents/operator/api/v1alpha1"
)

// statusPollInterval is how often a healthy FeedListener is re-reconciled.
//
// Every other input to this controller arrives as a watch event, but feed health does not: a
// relay going quiet changes nothing in the Kubernetes API, so nothing would wake the controller
// and the Streaming condition would stay stale indefinitely. Polling is the only way to observe
// it, and this interval bounds how long status can lie.
const statusPollInterval = 30 * time.Second

// StreamStatus is what a consumer reports about its subscription. It mirrors the JSON served by
// the ingestion container at /health/stream.
type StreamStatus struct {
	LastMessageUtc   *time.Time `json:"lastMessageUtc"`
	MessagesReceived int64      `json:"messagesReceived"`
	ShardIndex       int32      `json:"shardIndex"`
	ShardCount       int32      `json:"shardCount"`
}

// StreamProbe reads feed health from a single consumer pod.
type StreamProbe interface {
	Probe(ctx context.Context, podIP string) (*StreamStatus, error)
}

// HTTPStreamProbe polls each pod directly rather than going through the Service. A ClusterIP
// would answer from an arbitrary shard, which is exactly the wrong thing here: the controller
// needs to know whether *every* shard is receiving, not whether one of them is.
type HTTPStreamProbe struct {
	Client *http.Client
}

func NewHTTPStreamProbe() *HTTPStreamProbe {
	return &HTTPStreamProbe{Client: &http.Client{Timeout: 3 * time.Second}}
}

func (p *HTTPStreamProbe) Probe(ctx context.Context, podIP string) (*StreamStatus, error) {
	url := fmt.Sprintf("http://%s/health/stream", net.JoinHostPort(podIP, strconv.Itoa(containerPort)))
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return nil, err
	}

	resp, err := p.Client.Do(req)
	if err != nil {
		return nil, err
	}
	defer func() { _ = resp.Body.Close() }()

	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("probe %s: unexpected status %d", url, resp.StatusCode)
	}

	var status StreamStatus
	if err := json.NewDecoder(resp.Body).Decode(&status); err != nil {
		return nil, fmt.Errorf("probe %s: %w", url, err)
	}
	return &status, nil
}

// reconcileStatus derives status from the consumer pods and the feed health they report.
func (r *FeedListenerReconciler) reconcileStatus(ctx context.Context, fl *elitev1alpha1.FeedListener) (ctrl.Result, error) {
	log := logf.FromContext(ctx)

	var pods corev1.PodList
	if err := r.List(ctx, &pods,
		client.InNamespace(fl.Namespace),
		client.MatchingLabels(selectorLabels(fl)),
	); err != nil {
		return ctrl.Result{}, fmt.Errorf("listing consumer pods: %w", err)
	}

	var ready int32
	var lastMessage *time.Time
	streamingShards := 0

	for i := range pods.Items {
		pod := &pods.Items[i]
		if !isPodReady(pod) || pod.Status.PodIP == "" {
			continue
		}
		ready++

		status, err := r.StreamProbe.Probe(ctx, pod.Status.PodIP)
		if err != nil {
			// A pod that is Ready but unreachable is worth logging, not worth failing the
			// reconcile: readiness already gates traffic, and the Streaming condition below
			// will report the shortfall.
			log.V(1).Info("stream probe failed", "pod", pod.Name, "error", err)
			continue
		}
		if status.LastMessageUtc == nil {
			continue
		}
		if lastMessage == nil || status.LastMessageUtc.After(*lastMessage) {
			lastMessage = status.LastMessageUtc
		}
		if time.Since(*status.LastMessageUtc) <= fl.Spec.ReconnectAfterSilence.Duration {
			streamingShards++
		}
	}

	fl.Status.ObservedGeneration = fl.Generation
	fl.Status.DesiredConsumers = fl.Spec.Consumers
	fl.Status.ReadyConsumers = ready
	if lastMessage != nil {
		fl.Status.LastMessageTime = &metav1.Time{Time: *lastMessage}
	}

	setAvailability(fl, ready)
	streaming := setStreaming(fl, streamingShards)
	meta.RemoveStatusCondition(&fl.Status.Conditions, elitev1alpha1.ConditionDegraded)
	fl.Status.Phase = derivePhase(fl, ready, streaming)

	if err := r.Status().Update(ctx, fl); err != nil {
		return ctrl.Result{}, fmt.Errorf("updating status: %w", err)
	}

	return ctrl.Result{RequeueAfter: statusPollInterval}, nil
}

func setAvailability(fl *elitev1alpha1.FeedListener, ready int32) {
	available := metav1.ConditionFalse
	reason := "NoConsumersReady"
	message := fmt.Sprintf("0 of %d consumers ready", fl.Spec.Consumers)
	if ready > 0 {
		available = metav1.ConditionTrue
		reason = "ConsumersReady"
		message = fmt.Sprintf("%d of %d consumers ready", ready, fl.Spec.Consumers)
	}
	meta.SetStatusCondition(&fl.Status.Conditions, metav1.Condition{
		Type:               elitev1alpha1.ConditionAvailable,
		Status:             available,
		Reason:             reason,
		Message:            message,
		ObservedGeneration: fl.Generation,
	})

	progressing := metav1.ConditionFalse
	progressReason := "PartitionComplete"
	if ready < fl.Spec.Consumers {
		progressing = metav1.ConditionTrue
		progressReason = "ConsumersStarting"
	}
	meta.SetStatusCondition(&fl.Status.Conditions, metav1.Condition{
		Type:               elitev1alpha1.ConditionProgressing,
		Status:             progressing,
		Reason:             progressReason,
		Message:            message,
		ObservedGeneration: fl.Generation,
	})
}

// setStreaming reports on the subscription itself. It is deliberately stricter than
// availability: the partition is only whole when every shard is receiving, and a single silent
// shard means a slice of the feed is being dropped while the resource still looks healthy.
func setStreaming(fl *elitev1alpha1.FeedListener, streamingShards int) bool {
	streaming := int32(streamingShards) == fl.Spec.Consumers && fl.Spec.Consumers > 0

	status := metav1.ConditionFalse
	reason := "FeedSilent"
	message := fmt.Sprintf("%d of %d shards received a message within %s",
		streamingShards, fl.Spec.Consumers, fl.Spec.ReconnectAfterSilence.Duration)
	if streaming {
		status = metav1.ConditionTrue
		reason = "FeedActive"
	}

	meta.SetStatusCondition(&fl.Status.Conditions, metav1.Condition{
		Type:               elitev1alpha1.ConditionStreaming,
		Status:             status,
		Reason:             reason,
		Message:            message,
		ObservedGeneration: fl.Generation,
	})
	return streaming
}

func derivePhase(fl *elitev1alpha1.FeedListener, ready int32, streaming bool) string {
	switch {
	case ready == 0:
		return "Pending"
	case ready < fl.Spec.Consumers:
		return "Progressing"
	case streaming:
		return "Streaming"
	default:
		return "Silent"
	}
}

// markDegraded records a reconcile failure without disturbing the other conditions, so the last
// known availability and streaming state remain visible alongside the error.
func (r *FeedListenerReconciler) markDegraded(ctx context.Context, fl *elitev1alpha1.FeedListener, reason string, cause error) error {
	meta.SetStatusCondition(&fl.Status.Conditions, metav1.Condition{
		Type:               elitev1alpha1.ConditionDegraded,
		Status:             metav1.ConditionTrue,
		Reason:             reason,
		Message:            cause.Error(),
		ObservedGeneration: fl.Generation,
	})
	fl.Status.Phase = "Degraded"
	fl.Status.ObservedGeneration = fl.Generation
	return r.Status().Update(ctx, fl)
}

func isPodReady(pod *corev1.Pod) bool {
	if pod.Status.Phase != corev1.PodRunning {
		return false
	}
	for _, cond := range pod.Status.Conditions {
		if cond.Type == corev1.PodReady {
			return cond.Status == corev1.ConditionTrue
		}
	}
	return false
}
