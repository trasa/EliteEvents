using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EliteEvents.Eddn.Storage;

/// <summary>Outcome of one index rebuild, for logging and tests.</summary>
public readonly record struct IndexRebuildResult(int Added, int Removed, int Total)
{
    public bool ChangedAnything => Added > 0 || Removed > 0;
}

public interface ISearchIndexMaintainer
{
    Task<IndexRebuildResult> RebuildSystemIndexAsync(CancellationToken cancellationToken = default);

    Task<IndexRebuildResult> RebuildCarrierIndexAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reconciles <see cref="RedisKeys.SystemIndex"/> and <see cref="RedisKeys.CarrierIndex"/> against
/// the keys that actually exist. Ingestion owns this; the web tier only reads the result.
/// <para>
/// The indexes cannot maintain themselves the way every other key does. Their members are all
/// stored at score 0 because that is <c>ZRANGEBYLEX</c>'s precondition, which rules out scoring
/// them by last-seen time and pruning by score; and a TTL on the key would delete the whole index
/// rather than aging out its members. So the source of truth stays the data keys and their
/// existing 30-day TTL, and this reconciles against them rather than duplicating the expiry rule
/// in a second place where it could drift.
/// </para>
/// <para>
/// Rebuilding rather than incrementally pruning also covers three failures at once that would
/// each need their own mechanism: entries left behind when a system's data expires, entries
/// missing because the index was introduced after the data (the backfill case), and the index key
/// being lost entirely — which is a live possibility here, because the in-cluster Redis runs
/// <c>maxmemory-policy allkeys-lru</c> and will happily evict a single large key under pressure.
/// A rebuild is the same operation in all three cases.
/// </para>
/// </summary>
public class SearchIndexMaintainer : ISearchIndexMaintainer
{
    /// <summary>
    /// Members per ZADD/ZREM round-trip. Large enough that a rebuild is a handful of calls,
    /// small enough not to block the single-threaded server on one huge command.
    /// </summary>
    private const int WriteChunkSize = 500;

    private readonly ILogger<SearchIndexMaintainer> _logger;
    private readonly IServer _server;
    private readonly IDatabase _database;

    public SearchIndexMaintainer(ILogger<SearchIndexMaintainer> logger, IConnectionMultiplexer connection)
    {
        _logger = logger;
        // for SCAN
        _server = connection.GetServer(connection.GetEndPoints().First());
        // for everything else
        _database = connection.GetDatabase();
    }

    public Task<IndexRebuildResult> RebuildSystemIndexAsync(CancellationToken cancellationToken = default)
        => RebuildAsync(RedisKeys.SystemIndex, RedisKeys.AllSystemStationsPattern, "system", cancellationToken);

    public Task<IndexRebuildResult> RebuildCarrierIndexAsync(CancellationToken cancellationToken = default)
        => RebuildAsync(RedisKeys.CarrierIndex, RedisKeys.AllCarrierDaysPattern, "carrier", cancellationToken);

    private async Task<IndexRebuildResult> RebuildAsync(
        RedisKey index, string livePattern, string label, CancellationToken cancellationToken)
    {
        // Snapshot the index *before* scanning, not after. Anything the writer adds while the scan
        // is running is absent from this snapshot and therefore cannot be mistaken for a stale
        // entry and removed — which is the one way a rebuild could delete a live name.
        var indexed = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var entry in _database.SortedSetScanAsync(index).WithCancellation(cancellationToken))
        {
            indexed.Add(entry.Element.ToString());
        }

        var live = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var key in _server.KeysAsync(pattern: livePattern).WithCancellation(cancellationToken))
        {
            var name = RedisKeys.ExtractName(key.ToString());
            if (name is not null)
            {
                live.Add(name);
            }
        }

        var toAdd = live.Where(name => !indexed.Contains(name)).ToArray();
        var toRemove = indexed.Where(name => !live.Contains(name)).ToArray();

        foreach (var chunk in toAdd.Chunk(WriteChunkSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _database.SortedSetAddAsync(
                index, chunk.Select(name => new SortedSetEntry(name, RedisKeys.IndexScore)).ToArray());
        }

        foreach (var chunk in toRemove.Chunk(WriteChunkSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _database.SortedSetRemoveAsync(index, chunk.Select(name => (RedisValue)name).ToArray());
        }

        var result = new IndexRebuildResult(toAdd.Length, toRemove.Length, live.Count);
        if (result.ChangedAnything)
        {
            _logger.LogInformation(
                "Rebuilt {Label} search index: +{Added} -{Removed}, now {Total} entries",
                label, result.Added, result.Removed, result.Total);
        }
        else
        {
            _logger.LogDebug("{Label} search index already current, {Total} entries", label, result.Total);
        }

        return result;
    }
}
