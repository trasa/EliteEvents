using StackExchange.Redis;

namespace EliteEvents.Visitors.Services;

public class DockingRedisService
{
    private readonly ILogger<DockingRedisService> _logger;
    private readonly IServer _redisServer;
    private readonly IDatabase _redisDatabase;
    private static readonly TimeSpan Expiration = TimeSpan.FromDays(30);

    public DockingRedisService(ILogger<DockingRedisService> logger, IConnectionMultiplexer connection)
    {
        _logger = logger;
        // for KEYS, SCAN ...
        _redisServer = connection.GetServer(connection.GetEndPoints().First());
        // for everything else
        _redisDatabase = connection.GetDatabase();
    }

    public async Task RecordFleetCarrierDockingAsync(string carrierId, DateTimeOffset utcTimestamp)
    {
        var today = utcTimestamp.ToString("yyyy-MM-dd");
        var carrierKey = $"carrier:{carrierId}:daily:{today}";
        await _redisDatabase.StringIncrementAsync(carrierKey);
        await _redisDatabase.KeyExpireAsync(carrierKey, Expiration);
        // active days
        var carrierDaysKey = $"carrier:{carrierId}:days";
        await _redisDatabase.SortedSetAddAsync(carrierDaysKey, today, utcTimestamp.ToUnixTimeSeconds());
        await _redisDatabase.KeyExpireAsync(carrierDaysKey, Expiration);
    }

    public async Task RecordStationDockingAsync(string systemName, string stationName, string stationType, DateTimeOffset utcTimestamp)
    {
        var stationKey = $"system:{systemName}:station:{stationName}";
        await _redisDatabase.HashIncrementAsync(stationKey, "count");
        await _redisDatabase.HashSetAsync(stationKey, "type", stationType);
        await _redisDatabase.HashSetAsync(stationKey, "last_seen", utcTimestamp.ToUnixTimeSeconds());
        await _redisDatabase.KeyExpireAsync(stationKey, Expiration);

        // add station to system's station index sorted by last visit
        var systemStationsKey = $"system:{systemName}:stations";
        await _redisDatabase.SortedSetAddAsync(systemStationsKey, stationName, utcTimestamp.ToUnixTimeSeconds());
        await _redisDatabase.KeyExpireAsync(systemStationsKey, Expiration);
    }


    public async Task<List<StationDockingInfo>> GetSystemDockingAsync(string systemName)
    {
        var systemStationKey = $"system:{systemName}:stations";
        var stationNames = await _redisDatabase.SortedSetRangeByScoreAsync(systemStationKey, order: Order.Descending);
        var result = new List<StationDockingInfo>();
        foreach (var stationName in stationNames)
        {
            var stationKey = $"system:{systemName}:station:{stationName}";
            var stationData = await _redisDatabase.HashGetAllAsync(stationKey);
            if (stationData.Length == 0)
            {
                continue;
            }

            var dataDict = stationData.ToDictionary(x => x.Name.ToString(), x => x.Value.ToString());
            var dockingInfo = new StationDockingInfo
            {
                SystemName = systemName,
                StationName = stationName.ToString(),
                StationType = dataDict.GetValueOrDefault("type", ""),
                DockingCount = int.Parse(dataDict.GetValueOrDefault("count", "0")),
                LastSeen = DateTimeOffset.FromUnixTimeSeconds(long.Parse(dataDict.GetValueOrDefault("last_seen", "0")))
            };
            result.Add(dockingInfo);
        }

        return result;
    }

    public async Task<List<CarrierDockingInfo>> GetCarrierDockingAsync(string carrierId, int daysBack = 30)
    {
        var carrierDaysKey = $"carrier:{carrierId}:days";
        var activeDays = await _redisDatabase.SortedSetRangeByScoreAsync(carrierDaysKey, order: Order.Descending, take: daysBack);
        var result = new List<CarrierDockingInfo>();
        foreach (var day in activeDays)
        {
            var dayStr = day.ToString();
            var carrierKey = $"carrier:{carrierId}:daily:{dayStr}";
            var dockingCount = await _redisDatabase.StringGetAsync(carrierKey);
            if (dockingCount.HasValue)
            {

                var dockingInfo = new CarrierDockingInfo
                {
                    CarrierId = carrierId,
                    Date = DateTime.Parse(dayStr),
                    DockingCount = (int)dockingCount
                };
                result.Add(dockingInfo);
            }
        }

        return result;
    }

    public async Task<List<string>> GetMatchingCarriersAsync(string searchQuery)
    {
        if (string.IsNullOrWhiteSpace(searchQuery)) {
            return [];
        }

        var normalizedQuery = searchQuery.ToUpperInvariant().Trim();
        var matchingCarriers = new HashSet<string>();

        await foreach (var key in _redisServer.KeysAsync(pattern: $"carrier:*{normalizedQuery}*"))
        {
            var parts = key.ToString().Split(':');
            if (parts.Length > 1)
            {
                matchingCarriers.Add(parts[1]);
            }
        }
        return matchingCarriers.OrderBy(id => id).ToList();
    }
}

public class StationDockingInfo
{
    public string SystemName { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;
    public string StationType { get; set; } = string.Empty;
    public int DockingCount { get; set; }
    public DateTimeOffset LastSeen { get; set; }

    public override string ToString()
    {
        return $"{SystemName}: {StationName} ({StationType} - {DockingCount}";
    }
}

public class CarrierDockingInfo
{
    public string CarrierId { get; set; } = "";
    public DateTime Date { get; set; } = DateTime.MinValue;
    public int DockingCount { get; set; }
    public override string ToString()
    {
        return $"Fleet Carrier {CarrierId} - {Date:yyyy-MM-dd} - {DockingCount}";
    }
}
