using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace EliteEvents.Visitors.Services;

public class RedisHealthCheck : IHealthCheck
{
    // Real Elite Dangerous data keys. Deliberately excludes our own cache:* keys
    // so a stale cache can't mask a fully-evaporated database.
    private static readonly string[] DataKeyPatterns = ["system:*", "carrier:*"];

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
            // that *some* data exists, not how much.
            var server = _connection.GetServer(_connection.GetEndPoints().First());
            foreach (var pattern in DataKeyPatterns)
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