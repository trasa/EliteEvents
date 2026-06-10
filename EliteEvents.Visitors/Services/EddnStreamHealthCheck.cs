using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EliteEvents.Visitors.Services;

/// <summary>
/// Fails when the EDDN ZeroMQ stream has gone silent, catching a stalled receiver
/// even while 30-day-TTL data still lingers in Redis.
/// </summary>
public class EddnStreamHealthCheck : IHealthCheck
{
    private static readonly TimeSpan MaxSilence = TimeSpan.FromMinutes(5);

    private readonly StreamHealthTracker _tracker;

    public EddnStreamHealthCheck(StreamHealthTracker tracker)
    {
        _tracker = tracker;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var age = DateTimeOffset.UtcNow - _tracker.LastMessageUtc;

        var result = age > MaxSilence
            ? HealthCheckResult.Unhealthy(
                $"No EDDN message received for {age.TotalSeconds:F0}s (threshold {MaxSilence.TotalSeconds:F0}s)")
            : HealthCheckResult.Healthy($"Last EDDN message {age.TotalSeconds:F0}s ago");

        return Task.FromResult(result);
    }
}