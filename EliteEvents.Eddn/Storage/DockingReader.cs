using StackExchange.Redis;

namespace EliteEvents.Eddn.Storage;

/// <summary>
/// Read side of the Redis schema, used by the web tier. Every method here is a pure read —
/// nothing on this interface mutates state, so web pods can scale freely.
/// </summary>
public interface IDockingReader
{
    Task<IReadOnlyList<StationDockingInfo>> GetSystemDockingAsync(string systemName);

    Task<IReadOnlyList<CarrierDockingInfo>> GetCarrierDockingAsync(string carrierId, int daysBack = 30);

    Task<IReadOnlyList<SystemVisitInfo>> GetSystemVisitsAsync(int topN = 100);

    Task<IReadOnlyList<string>> GetMatchingSystemsAsync(string searchQuery, int limit = DockingReader.DefaultSearchLimit);

    Task<IReadOnlyList<string>> GetMatchingCarriersAsync(string searchQuery, int limit = DockingReader.DefaultSearchLimit);
}

public class DockingReader : IDockingReader
{
    /// <summary>
    /// Results returned by a search when the caller doesn't say. Searches used to be unbounded;
    /// a page of results is all the UI can use, and the cap is what lets the prefix lookup stop
    /// early instead of walking the whole matching range.
    /// </summary>
    public const int DefaultSearchLimit = 200;

    /// <summary>
    /// Hard ceiling on members collected during the substring fallback, so a very broad query
    /// (a single character against the carrier index, which has no minimum length) cannot pull an
    /// unbounded list into memory just to sort it and throw nearly all of it away.
    /// </summary>
    private const int SubstringScanCeiling = 5_000;

    /// <summary>
    /// Exclusive lower bound on a leaderboard entry's score. A system with a single recorded visit
    /// is noise, so the leaderboard starts just above it. This is a presentation rule rather than
    /// part of the key schema, which is why it lives here and not in <see cref="RedisKeys"/>.
    /// </summary>
    private const double LeaderboardScoreFloor = 1.0;

    private readonly IDatabase _redisDatabase;

    public DockingReader(IConnectionMultiplexer connection)
    {
        _redisDatabase = connection.GetDatabase();
    }

    public async Task<IReadOnlyList<StationDockingInfo>> GetSystemDockingAsync(string systemName)
    {
        var system = RedisKeys.NormalizeSystem(systemName);
        var stationNames = await _redisDatabase.SortedSetRangeByScoreAsync(
            RedisKeys.SystemStations(system), order: Order.Descending);

        var result = new List<StationDockingInfo>();
        foreach (var stationName in stationNames)
        {
            var stationData = await _redisDatabase.HashGetAllAsync(RedisKeys.Station(system, stationName!));
            if (stationData.Length == 0)
            {
                continue;
            }

            var dataDict = stationData.ToDictionary(x => x.Name.ToString(), x => x.Value.ToString());
            result.Add(new StationDockingInfo
            {
                SystemName = system,
                StationName = stationName.ToString(),
                StationType = dataDict.GetValueOrDefault(RedisKeys.StationTypeField, ""),
                DockingCount = int.Parse(dataDict.GetValueOrDefault(RedisKeys.StationCountField, "0")),
                LastSeen = DateTimeOffset.FromUnixTimeSeconds(
                    long.Parse(dataDict.GetValueOrDefault(RedisKeys.StationLastSeenField, "0")))
            });
        }

        return result;
    }

    public async Task<IReadOnlyList<CarrierDockingInfo>> GetCarrierDockingAsync(string carrierId, int daysBack = 30)
    {
        var carrier = RedisKeys.NormalizeCarrier(carrierId);
        var activeDays = await _redisDatabase.SortedSetRangeByScoreAsync(
            RedisKeys.CarrierDays(carrier), order: Order.Descending, take: daysBack);

        var result = new List<CarrierDockingInfo>();
        foreach (var day in activeDays)
        {
            var dayStr = day.ToString();
            var dockingCount = await _redisDatabase.StringGetAsync(RedisKeys.CarrierDaily(carrier, dayStr));
            if (dockingCount.HasValue)
            {
                result.Add(new CarrierDockingInfo
                {
                    CarrierId = carrier,
                    Date = DateTime.Parse(dayStr),
                    DockingCount = (int)dockingCount
                });
            }
        }

        return result;
    }

    /// <summary>
    /// The most-visited leaderboard, highest first.
    /// <para>
    /// Both the row limit and the "more than one visit" filter are applied <em>server-side</em>,
    /// which is load-bearing rather than a micro-optimisation. This used to fetch the whole sorted
    /// set by rank (<c>0..-1 WITHSCORES</c>) and trim it in LINQ, so rendering a 25-row panel
    /// pulled every member across the wire. <c>systems:visits</c> gains a member per distinct
    /// system jumped to and is only reset weekly, so that grew to ~231k members / ~20 MB by
    /// mid-week — measured at ~600 ms per call against ~10 ms for a bounded one. Redis is
    /// single-threaded, so each of those was a full server stall, and the home page polls this
    /// every 15 seconds per open tab. The replies also buffered per-client without a cap, which is
    /// what pushed the instance past <c>maxmemory</c> and into <c>allkeys-lru</c> eviction.
    /// </para>
    /// <para>
    /// <c>ZREVRANGEBYSCORE key +inf (1 WITHSCORES LIMIT 0 topN</c> is O(log N + topN) and returns
    /// exactly what the old client-side <c>Where</c>/<c>Take</c> pair produced: a single visit is
    /// noise — usually one commander passing through — so the leaderboard only counts systems seen
    /// more than once, and <see cref="Exclude.Start"/> is what makes that bound strict.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<SystemVisitInfo>> GetSystemVisitsAsync(int topN = 100)
    {
        if (topN <= 0)
        {
            return [];
        }

        var entries = await _redisDatabase.SortedSetRangeByScoreWithScoresAsync(
            RedisKeys.SystemVisits,
            start: LeaderboardScoreFloor,
            stop: double.PositiveInfinity,
            exclude: Exclude.Start,
            order: Order.Descending,
            skip: 0,
            take: topN
        );

        return entries
            .Select(entry => new SystemVisitInfo(entry.Element.ToString(), (long)entry.Score))
            .ToList();
    }

    public Task<IReadOnlyList<string>> GetMatchingCarriersAsync(string searchQuery, int limit = DefaultSearchLimit)
        => SearchIndexAsync(RedisKeys.CarrierIndex, searchQuery, limit);

    public Task<IReadOnlyList<string>> GetMatchingSystemsAsync(string searchQuery, int limit = DefaultSearchLimit)
        => SearchIndexAsync(RedisKeys.SystemIndex, searchQuery, limit);

    /// <summary>
    /// Searches one of the name indexes, prefix matches first.
    /// <para>
    /// This replaced a <c>SCAN</c> of the entire keyspace with a <c>*glob*</c> pattern, which cost
    /// O(keyspace) per search — and the keyspace is dominated by per-station hashes, so it was far
    /// larger than the set of names being searched.
    /// </para>
    /// <para>
    /// Two passes, in order of both cost and relevance:
    /// </para>
    /// <list type="number">
    /// <item><c>ZRANGEBYLEX</c> for names starting with the query — O(log N + M), and the only
    /// pass a typeahead needs.</item>
    /// <item><c>ZSCAN</c> with a <c>*query*</c> MATCH for names merely containing it, run only
    /// when the first pass didn't fill the page. Still O(index), but the index holds one member
    /// per system rather than one key per station, and matching happens server-side so only hits
    /// cross the wire.</item>
    /// </list>
    /// <para>
    /// Ordering changed with it: prefix matches now come first, where the old SCAN returned one
    /// flat alphabetical list. Searching "SOL" putting SOL above ANTLIA SECTOR SOL-A is the point.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<string>> SearchIndexAsync(RedisKey index, string searchQuery, int limit)
    {
        if (string.IsNullOrWhiteSpace(searchQuery) || limit <= 0)
        {
            return [];
        }

        var query = RedisKeys.NormalizeQuery(searchQuery);
        if (query.Length == 0)
        {
            return [];
        }

        var prefixMatches = await _redisDatabase.SortedSetRangeByValueAsync(
            index,
            RedisKeys.LexPrefixMin(query),
            RedisKeys.LexPrefixMax(query),
            Exclude.None,
            Order.Ascending,
            skip: 0,
            take: limit);

        var results = new List<string>(limit);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var match in prefixMatches)
        {
            var name = match.ToString();
            if (seen.Add(name))
            {
                results.Add(name);
            }
        }

        if (results.Count >= limit)
        {
            return results;
        }

        // Substring pass. ZSCAN gives no ordering guarantee and can return the same member twice
        // across cursor pages, so results are collected, de-duplicated against the prefix hits,
        // and sorted before being appended.
        var substringMatches = new List<string>();
        await foreach (var entry in _redisDatabase.SortedSetScanAsync(index, RedisKeys.IndexMatchPattern(query)))
        {
            var name = entry.Element.ToString();
            if (seen.Add(name))
            {
                substringMatches.Add(name);
            }

            if (substringMatches.Count >= SubstringScanCeiling)
            {
                break;
            }
        }

        substringMatches.Sort(StringComparer.Ordinal);
        results.AddRange(substringMatches.Take(limit - results.Count));
        return results;
    }
}
