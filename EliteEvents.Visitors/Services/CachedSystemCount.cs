using StackExchange.Redis;

namespace EliteEvents.Visitors.Services;

public class CachedSystemCount
{
    private readonly ILogger<CachedSystemCount> _logger;
    private readonly IServer _server;
    private readonly IDatabase _database;
    private const string CachedCountKey = "cache:system:count";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

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
        var cached = await _database.StringGetAsync(CachedCountKey);
        if (cached.HasValue)
        {
            return (long)cached;
        }

        var count = await CalculateActualCountAsync();
        await _database.StringSetAsync(CachedCountKey, count, CacheDuration);
        return count;
    }

    private async Task<long> CalculateActualCountAsync()
    {
        long count = 0;
        await foreach (var key in _server.KeysAsync(pattern: "system:*:stations"))
        {
            count++;
        }
        _logger.LogInformation("Calculated actual system count: {Count}", count);
        return count;
    }
}
