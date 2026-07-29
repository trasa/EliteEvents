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
            await _connection.GetDatabase().PingAsync();

            // SCAN for the first matching key and short-circuit; we only need to know
            // that *some* data exists, not how much. RedisKeys.DataKeyPatterns deliberately
            // excludes cache:* so a stale cache can't mask a fully-evaporated database.
            var server = _connection.GetServer(_connection.GetEndPoints().First());
            foreach (var pattern in RedisKeys.DataKeyPatterns)
            {
                await foreach (var key in server.KeysAsync(pattern: pattern, pageSize: 1)
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
