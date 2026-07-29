using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EliteEvents.Eddn.Storage;

public interface ICachedSystemCount
{
    Task<long> GetSystemCountAsync();
}

/// <summary>
/// Total number of systems with recorded station activity. Counting means SCANning the whole
/// keyspace, so the result is cached in Redis for
/// <see cref="RedisKeys.SystemCountCacheDuration"/> rather than in process memory — the cache
/// then stays coherent across web pods and survives a restart.
/// <para>
/// This is the one thing on the read side that writes, and it only ever touches its own
/// <see cref="RedisKeys.SystemCountCache"/> key, never real data.
/// </para>
/// </summary>
public class CachedSystemCount : ICachedSystemCount
{
    private readonly ILogger<CachedSystemCount> _logger;
    private readonly IServer _server;
    private readonly IDatabase _database;

    public CachedSystemCount(ILogger<CachedSystemCount> logger, IConnectionMultiplexer connection)
    {
        _logger = logger;
        // for KEYS, SCAN ...
        _server = connection.GetServer(connection.GetEndPoints().First());
        // for everything else
        _database = connection.GetDatabase();
    }

    public async Task<long> GetSystemCountAsync()
    {
        var cached = await _database.StringGetAsync(RedisKeys.SystemCountCache);
        if (cached.HasValue)
        {
            return (long)cached;
        }

        var count = await CalculateActualCountAsync();
        await _database.StringSetAsync(RedisKeys.SystemCountCache, count, RedisKeys.SystemCountCacheDuration);
        return count;
    }

    private async Task<long> CalculateActualCountAsync()
    {
        long count = 0;
        await foreach (var _ in _server.KeysAsync(pattern: RedisKeys.AllSystemStationsPattern))
        {
            count++;
        }

        _logger.LogInformation("Calculated actual system count: {Count}", count);
        return count;
    }
}
