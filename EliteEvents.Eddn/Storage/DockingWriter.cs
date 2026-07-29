using StackExchange.Redis;

namespace EliteEvents.Eddn.Storage;

/// <summary>
/// Write side of the Redis schema. Only the ingestion service takes a dependency on this —
/// the web tier is read-only by construction.
/// </summary>
public interface IDockingWriter
{
    Task RecordFleetCarrierDockingAsync(string carrierId, DateTimeOffset utcTimestamp);

    Task RecordStationDockingAsync(string systemName, string stationName, string stationType, DateTimeOffset utcTimestamp);

    Task RecordSystemVisitAsync(string systemName);
}

public class DockingWriter : IDockingWriter
{
    private readonly WeeklyExpirationCalculator _weeklyExpirationCalculator;
    private readonly IDatabase _redisDatabase;

    public DockingWriter(IConnectionMultiplexer connection, WeeklyExpirationCalculator weeklyExpirationCalculator)
    {
        _weeklyExpirationCalculator = weeklyExpirationCalculator;
        _redisDatabase = connection.GetDatabase();
    }

    public async Task RecordFleetCarrierDockingAsync(string carrierId, DateTimeOffset utcTimestamp)
    {
        var carrier = RedisKeys.NormalizeCarrier(carrierId);
        var today = utcTimestamp.ToString(RedisKeys.DateFormat);

        var carrierKey = RedisKeys.CarrierDaily(carrier, today);
        await _redisDatabase.StringIncrementAsync(carrierKey);
        await _redisDatabase.KeyExpireAsync(carrierKey, RedisKeys.DataExpiration);

        // active days
        var carrierDaysKey = RedisKeys.CarrierDays(carrier);
        await _redisDatabase.SortedSetAddAsync(carrierDaysKey, today, utcTimestamp.ToUnixTimeSeconds());
        await _redisDatabase.KeyExpireAsync(carrierDaysKey, RedisKeys.DataExpiration);
    }

    public async Task RecordStationDockingAsync(string systemName, string stationName, string stationType, DateTimeOffset utcTimestamp)
    {
        var system = RedisKeys.NormalizeSystem(systemName);

        var stationKey = RedisKeys.Station(system, stationName);
        await _redisDatabase.HashIncrementAsync(stationKey, RedisKeys.StationCountField);
        await _redisDatabase.HashSetAsync(stationKey, RedisKeys.StationTypeField, stationType);
        await _redisDatabase.HashSetAsync(stationKey, RedisKeys.StationLastSeenField, utcTimestamp.ToUnixTimeSeconds());
        await _redisDatabase.KeyExpireAsync(stationKey, RedisKeys.DataExpiration);

        // add station to system's station index sorted by last visit
        var systemStationsKey = RedisKeys.SystemStations(system);
        await _redisDatabase.SortedSetAddAsync(systemStationsKey, stationName, utcTimestamp.ToUnixTimeSeconds());
        await _redisDatabase.KeyExpireAsync(systemStationsKey, RedisKeys.DataExpiration);
    }

    public async Task RecordSystemVisitAsync(string systemName)
    {
        var system = RedisKeys.NormalizeSystem(systemName);
        // system visit leaderboard (expires at 0730 UTC Thursday)
        await _redisDatabase.SortedSetIncrementAsync(RedisKeys.SystemVisits, system, 1);
        await _redisDatabase.KeyExpireAsync(RedisKeys.SystemVisits, _weeklyExpirationCalculator.GetNextExpirationUtc(DateTime.UtcNow));
    }
}
