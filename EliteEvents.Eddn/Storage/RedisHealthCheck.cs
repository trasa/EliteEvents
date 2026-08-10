using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace EliteEvents.Eddn.Storage;

/// <summary>
/// Reports Redis as healthy only when it is reachable <em>and</em> holds real Elite Dangerous
/// data. Both the ingestion service and the web tier use it, so it lives here rather than in
/// either app.
/// </summary>
public class RedisHealthCheck : IHealthCheck
{
    /// <summary>
    /// SCAN <c>COUNT</c> for the data-presence probe.
    /// <para>
    /// This is a page size, not a result limit — the iteration still short-circuits on the first
    /// match. It was 1, which made <c>KeysAsync</c> a chain of one network round trip per hash
    /// bucket, each of which can queue behind whatever else is in flight on the multiplexer. That
    /// turned a yes/no check into tens of serial round trips against a probe budget measured in
    /// seconds. One page comfortably larger than the bucket count needed to find a match makes it
    /// one round trip in the normal case, and the server-side MATCH means only hits cross the
    /// wire either way.
    /// </para>
    /// </summary>
    private const int ScanPageSize = 250;

    private readonly IConnectionMultiplexer _connection;

    public RedisHealthCheck(IConnectionMultiplexer connection)
    {
        _connection = connection;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // The token is what bounds this check: without it a stalled server is a hung probe
            // rather than a failed one, and the caller's timeout never fires.
            cancellationToken.ThrowIfCancellationRequested();
            await _connection.GetDatabase().PingAsync();

            // SCAN for the first matching key and short-circuit; we only need to know
            // that *some* data exists, not how much. RedisKeys.DataKeyPatterns deliberately
            // excludes cache:* so a stale cache can't mask a fully-evaporated database.
            var server = _connection.GetServer(_connection.GetEndPoints().First());
            foreach (var pattern in RedisKeys.DataKeyPatterns)
            {
                await foreach (var key in server.KeysAsync(pattern: pattern, pageSize: ScanPageSize)
                                   .WithCancellation(cancellationToken))
                {
                    return HealthCheckResult.Healthy($"Redis reachable, data present (matched {pattern})");
                }
            }

            return HealthCheckResult.Unhealthy("Redis reachable but no Elite Dangerous data present");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Redis connectivity check failed", ex);
        }
    }
}
