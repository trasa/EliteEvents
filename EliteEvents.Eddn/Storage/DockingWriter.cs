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

/// <summary>
/// <para>
/// Every method here dispatches its writes as a single <see cref="IBatch"/>. The commands are
/// identical to the ones that were previously awaited one at a time; batching only changes how
/// they reach the server. A station docking was six sequential round-trips, so at EDDN's steady
/// rate of several events a second the writer spent nearly all of its time waiting on the
/// network — and every one of those waits was a chance for the next message to queue up behind
/// it.
/// </para>
/// <para>
/// A batch is <em>not</em> a transaction: the commands are pipelined together and are not
/// isolated from other clients, so another writer could interleave. That is fine here because
/// ingestion is a single replica by design (<c>replicas: 1</c>, <c>strategy: Recreate</c>) and
/// because every operation is a commutative increment or an idempotent set. Nothing depends on
/// reading a value back before writing it, which is what would actually require MULTI/EXEC.
/// </para>
/// </summary>
public class DockingWriter : IDockingWriter
{
    private readonly WeeklyExpirationCalculator _weeklyExpirationCalculator;
    private readonly IDatabase _redisDatabase;

    public DockingWriter(IConnectionMultiplexer connection, WeeklyExpirationCalculator weeklyExpirationCalculator)
    {
        _weeklyExpirationCalculator = weeklyExpirationCalculator;
        _redisDatabase = connection.GetDatabase();
    }

    public Task RecordFleetCarrierDockingAsync(string carrierId, DateTimeOffset utcTimestamp)
    {
        var carrier = RedisKeys.NormalizeCarrier(carrierId);
        var today = utcTimestamp.ToString(RedisKeys.DateFormat);
        var timestamp = utcTimestamp.ToUnixTimeSeconds();

        var carrierKey = RedisKeys.CarrierDaily(carrier, today);
        var carrierDaysKey = RedisKeys.CarrierDays(carrier);

        var batch = _redisDatabase.CreateBatch();
        var pending = new Task[]
        {
            batch.StringIncrementAsync(carrierKey),
            batch.KeyExpireAsync(carrierKey, RedisKeys.DataExpiration),

            // active days
            batch.SortedSetAddAsync(carrierDaysKey, today, timestamp),
            batch.KeyExpireAsync(carrierDaysKey, RedisKeys.DataExpiration),

            // searchable name; the index carries no TTL of its own, see SearchIndexMaintainer
            batch.SortedSetAddAsync(RedisKeys.CarrierIndex, carrier, RedisKeys.IndexScore)
        };

        return ExecuteAsync(batch, pending);
    }

    public Task RecordStationDockingAsync(string systemName, string stationName, string stationType, DateTimeOffset utcTimestamp)
    {
        var system = RedisKeys.NormalizeSystem(systemName);
        var timestamp = utcTimestamp.ToUnixTimeSeconds();

        var stationKey = RedisKeys.Station(system, stationName);
        var systemStationsKey = RedisKeys.SystemStations(system);

        var batch = _redisDatabase.CreateBatch();
        var pending = new Task[]
        {
            batch.HashIncrementAsync(stationKey, RedisKeys.StationCountField),

            // One HSET for both fields rather than two: same result, one fewer command.
            batch.HashSetAsync(stationKey,
            [
                new HashEntry(RedisKeys.StationTypeField, stationType),
                new HashEntry(RedisKeys.StationLastSeenField, timestamp)
            ]),
            batch.KeyExpireAsync(stationKey, RedisKeys.DataExpiration),

            // add station to system's station index sorted by last visit
            batch.SortedSetAddAsync(systemStationsKey, stationName, timestamp),
            batch.KeyExpireAsync(systemStationsKey, RedisKeys.DataExpiration),

            // searchable name; the index carries no TTL of its own, see SearchIndexMaintainer
            batch.SortedSetAddAsync(RedisKeys.SystemIndex, system, RedisKeys.IndexScore)
        };

        return ExecuteAsync(batch, pending);
    }

    public Task RecordSystemVisitAsync(string systemName)
    {
        var system = RedisKeys.NormalizeSystem(systemName);

        // Deliberately does not touch index:systems. Only station activity makes a system
        // searchable — a system that has merely been jumped through has no stations page to
        // land on, and indexing it here would also inflate the system count, which counts
        // systems with station activity.
        var batch = _redisDatabase.CreateBatch();
        var pending = new Task[]
        {
            // system visit leaderboard (expires at 0730 UTC Thursday)
            batch.SortedSetIncrementAsync(RedisKeys.SystemVisits, system, 1),
            batch.KeyExpireAsync(RedisKeys.SystemVisits, _weeklyExpirationCalculator.GetNextExpirationUtc(DateTime.UtcNow))
        };

        return ExecuteAsync(batch, pending);
    }

    /// <summary>
    /// Dispatches a queued batch and waits for every reply.
    /// <para>
    /// The ordering matters: the commands are only queued when their methods are called, and
    /// nothing is sent until <see cref="IBatch.Execute"/>. Awaiting any of the returned tasks
    /// before that call would deadlock, which is why they are collected into an array first and
    /// awaited only here.
    /// </para>
    /// </summary>
    private static Task ExecuteAsync(IBatch batch, Task[] pending)
    {
        batch.Execute();
        return Task.WhenAll(pending);
    }
}
