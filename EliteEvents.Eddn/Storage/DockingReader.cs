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

    Task<IReadOnlyList<string>> GetMatchingSystemsAsync(string searchQuery);

    Task<IReadOnlyList<string>> GetMatchingCarriersAsync(string searchQuery);
}

public class DockingReader : IDockingReader
{
    private readonly IServer _redisServer;
    private readonly IDatabase _redisDatabase;

    public DockingReader(IConnectionMultiplexer connection)
    {
        // for KEYS, SCAN ...
        _redisServer = connection.GetServer(connection.GetEndPoints().First());
        // for everything else
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

    public async Task<IReadOnlyList<SystemVisitInfo>> GetSystemVisitsAsync(int topN = 100)
    {
        var entries = await _redisDatabase.SortedSetRangeByRankWithScoresAsync(
            RedisKeys.SystemVisits,
            start: 0,
            stop: -1,
            order: Order.Descending
        );

        // A single visit is noise — usually one commander passing through — so the leaderboard
        // only counts systems seen more than once.
        return entries
            .Where(entry => entry.Score > 1.0)
            .Select(entry => new SystemVisitInfo(entry.Element.ToString(), (long)entry.Score))
            .Take(topN)
            .ToList();
    }

    public async Task<IReadOnlyList<string>> GetMatchingCarriersAsync(string searchQuery)
    {
        if (string.IsNullOrWhiteSpace(searchQuery))
        {
            return [];
        }

        var pattern = RedisKeys.CarrierSearchPattern(RedisKeys.NormalizeQuery(searchQuery));
        return await ScanForNamesAsync(pattern);
    }

    public async Task<IReadOnlyList<string>> GetMatchingSystemsAsync(string searchQuery)
    {
        if (string.IsNullOrWhiteSpace(searchQuery))
        {
            return [];
        }

        var pattern = RedisKeys.SystemSearchPattern(RedisKeys.NormalizeQuery(searchQuery));
        return await ScanForNamesAsync(pattern);
    }

    /// <summary>
    /// SCANs for keys matching <paramref name="pattern"/> and collects the distinct name segment.
    /// Several keys share one name (a system has an index plus one key per station), hence the set.
    /// </summary>
    private async Task<IReadOnlyList<string>> ScanForNamesAsync(string pattern)
    {
        var matches = new HashSet<string>();
        await foreach (var key in _redisServer.KeysAsync(pattern: pattern))
        {
            var name = RedisKeys.ExtractName(key.ToString());
            if (name is not null)
            {
                matches.Add(name);
            }
        }

        return matches.OrderBy(name => name).ToList();
    }
}
