using EliteEvents.Eddn.Config;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace EliteEvents.Eddn.Storage;

/// <summary>
/// Fails only when Redis has been unreachable long enough that the client is not coming back —
/// the one condition worth restarting a pod over. See <see cref="RedisConnectivityState"/> for
/// the outage that motivated it.
/// </summary>
/// <remarks>
/// This reads an in-memory timestamp and never touches Redis. That is deliberate: the failure it
/// detects is a Redis client that hangs instead of failing, so a liveness probe that called Redis
/// could hang on exactly the thing it is meant to catch, and a probe that never answers is a
/// probe that never trips. Something else does the calling — <c>RedisConnectivityMonitor</c> in
/// the web host — and its results land here.
/// </remarks>
public class RedisLivenessHealthCheck : IHealthCheck
{
    private readonly RedisConnectivityState _state;
    private readonly RedisLivenessOptions _options;

    public RedisLivenessHealthCheck(RedisConnectivityState state, IOptions<RedisLivenessOptions> options)
    {
        _state = state;
        _options = options.Value;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var threshold = _options.UnreachableRestartThreshold;
        var unreachableFor = _state.UnreachableFor(now);

        if (_state.IsStuck(now, threshold))
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Redis unreachable for {unreachableFor.TotalMinutes:F1} min " +
                $"(threshold {threshold.TotalMinutes:F0} min) — restarting is the only recovery"));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            $"Redis last reachable {unreachableFor.TotalSeconds:F0}s ago"));
    }
}
