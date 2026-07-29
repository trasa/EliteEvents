using EliteEvents.Eddn.Config;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace EliteEvents.Eddn.Storage;

/// <summary>
/// Fails when the EDDN ZeroMQ stream has gone silent, catching a stalled receiver even while
/// 30-day-TTL data still lingers in Redis.
/// <para>
/// It lives in the storage layer rather than in the ingestion service because the signal it
/// reads is now a Redis key: the ingestion container writes the heartbeat, and both containers
/// (plus any uptime monitor) read it. The ingestion service reading its own heartbeat back is
/// deliberate — that makes readiness cover the whole write-and-read round trip.
/// </para>
/// </summary>
public class EddnStreamHealthCheck : IHealthCheck
{
    private readonly IStreamHeartbeatReader _heartbeat;
    private readonly EddnOptions _options;

    public EddnStreamHealthCheck(IStreamHeartbeatReader heartbeat, IOptions<EddnOptions> options)
    {
        _heartbeat = heartbeat;
        _options = options.Value;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // The threshold is the same one the receiver reconnects on, so the check goes unhealthy
        // exactly when automatic recovery starts. That is what readiness should say; keeping the
        // liveness probe off this check is what stops k8s restarting a pod mid-recovery.
        var maxSilence = _options.ReconnectAfterSilence;

        DateTimeOffset? lastMessage;
        try
        {
            lastMessage = await _heartbeat.GetLastMessageUtcAsync();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Could not read the EDDN heartbeat from Redis", ex);
        }

        if (lastMessage is null)
        {
            return HealthCheckResult.Unhealthy("No EDDN heartbeat recorded");
        }

        var age = DateTimeOffset.UtcNow - lastMessage.Value;

        return age > maxSilence
            ? HealthCheckResult.Unhealthy(
                $"No EDDN message received for {age.TotalSeconds:F0}s (threshold {maxSilence.TotalSeconds:F0}s)")
            : HealthCheckResult.Healthy($"Last EDDN message {age.TotalSeconds:F0}s ago");
    }
}