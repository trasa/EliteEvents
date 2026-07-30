using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EliteEvents.Eddn.Storage;

public interface ISearchIndexPurger
{
    /// <summary>Deletes the keys a listener leaves behind. Returns how many were removed.</summary>
    Task<long> PurgeAsync();
}

/// <summary>
/// Removes the Redis state that outlives a feed listener.
/// <para>
/// Everything else this application writes carries a rolling 30-day TTL and cleans itself up,
/// but the search indexes cannot: <c>ZRANGEBYLEX</c> is only defined when every member scores
/// the same, so the index cannot also carry a timestamp to be pruned by, and a TTL on the key
/// would drop the entire index rather than stale members. They stay correct only because
/// <see cref="SearchIndexMaintainer"/> reconciles them, which stops the moment the listener
/// does — leaving keys that nothing owns and nothing will ever reclaim.
/// </para>
/// <para>
/// This runs in the ingestion image rather than in the operator so that <see cref="RedisKeys"/>
/// stays the only definition of the keyspace. A controller deleting <c>index:systems</c> by
/// name in Go would be a second, silently drifting copy of the one contract the containers
/// share.
/// </para>
/// </summary>
public class SearchIndexPurger : ISearchIndexPurger
{
    private readonly ILogger<SearchIndexPurger> _logger;
    private readonly IConnectionMultiplexer _connection;

    public SearchIndexPurger(ILogger<SearchIndexPurger> logger, IConnectionMultiplexer connection)
    {
        _logger = logger;
        _connection = connection;
    }

    public async Task<long> PurgeAsync()
    {
        var db = _connection.GetDatabase();

        // The heartbeat goes too: it is the web tier's evidence that ingestion is alive, and a
        // stale one left behind would read as a stalled listener rather than an absent one.
        RedisKey[] keys =
        [
            RedisKeys.SystemIndex,
            RedisKeys.CarrierIndex,
            RedisKeys.EddnHeartbeat
        ];

        var removed = await db.KeyDeleteAsync(keys);
        _logger.LogInformation("Purged {Removed} of {Total} listener keys: {Keys}",
            removed, keys.Length, string.Join(", ", keys.Select(k => k.ToString())));

        return removed;
    }
}
