using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EliteEvents.Eddn.Storage;

public interface ICachedSystemCount
{
    Task<long> GetSystemCountAsync();
}

/// <summary>
/// Total number of systems with recorded station activity.
/// <para>
/// This used to mean SCANning the whole keyspace for <c>system:*:stations</c>, which is why the
/// result is cached. Now that <see cref="RedisKeys.SystemIndex"/> holds exactly one member per
/// such system, the count is a single O(1) <c>ZCARD</c> and the cache is no longer load-bearing —
/// it is kept because it still saves a round-trip on a value rendered on every page, and because
/// caching in Redis rather than in process memory is what keeps it coherent across web pods.
/// </para>
/// <para>
/// This is the one thing on the read side that writes, and it only ever touches its own
/// <see cref="RedisKeys.SystemCountCache"/> key, never real data.
/// </para>
/// </summary>
public class CachedSystemCount : ICachedSystemCount
{
    private readonly ILogger<CachedSystemCount> _logger;
    private readonly IDatabase _database;

    public CachedSystemCount(ILogger<CachedSystemCount> logger, IConnectionMultiplexer connection)
    {
        _logger = logger;
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
        var count = await _database.SortedSetLengthAsync(RedisKeys.SystemIndex);
        _logger.LogInformation("Calculated actual system count: {Count}", count);
        return count;
    }
}
