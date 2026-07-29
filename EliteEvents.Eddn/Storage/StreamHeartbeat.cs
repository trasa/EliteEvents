using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EliteEvents.Eddn.Storage;

/// <summary>
/// Write side of the EDDN liveness signal. Only the ingestion service records heartbeats.
/// </summary>
public interface IStreamHeartbeatWriter
{
    /// <summary>
    /// Records that a message arrived at <paramref name="lastMessageUtc"/>. Writes are throttled
    /// to <see cref="RedisKeys.HeartbeatWriteInterval"/>; pass <paramref name="force"/> to write
    /// regardless, which the receiver does once at startup to seed the grace window.
    /// </summary>
    Task RecordAsync(DateTimeOffset lastMessageUtc, bool force = false);
}

/// <summary>
/// Read side of the EDDN liveness signal, used by health checks in both containers.
/// </summary>
public interface IStreamHeartbeatReader
{
    /// <summary>When EDDN last delivered a message, or null if no heartbeat has been recorded.</summary>
    Task<DateTimeOffset?> GetLastMessageUtcAsync();
}

public class RedisStreamHeartbeat : IStreamHeartbeatWriter, IStreamHeartbeatReader
{
    private readonly ILogger<RedisStreamHeartbeat> _logger;
    private readonly IDatabase _database;

    /// <summary>Timestamp of the last successful write, for throttling. Ticks so it can be interlocked.</summary>
    private long _lastWrittenTicks;

    public RedisStreamHeartbeat(ILogger<RedisStreamHeartbeat> logger, IConnectionMultiplexer connection)
    {
        _logger = logger;
        _database = connection.GetDatabase();
    }

    public async Task RecordAsync(DateTimeOffset lastMessageUtc, bool force = false)
    {
        var previous = Interlocked.Read(ref _lastWrittenTicks);
        if (!force && lastMessageUtc.UtcTicks - previous < RedisKeys.HeartbeatWriteInterval.Ticks)
        {
            return;
        }

        Interlocked.Exchange(ref _lastWrittenTicks, lastMessageUtc.UtcTicks);
        try
        {
            await _database.StringSetAsync(RedisKeys.EddnHeartbeat,
                lastMessageUtc.ToUnixTimeMilliseconds(), RedisKeys.HeartbeatExpiration);
        }
        catch (Exception ex)
        {
            // Deliberately leave the throttle advanced: while Redis is unreachable this caps the
            // retries — and the log noise — at one per interval, and the next successful write
            // carries the current message time anyway, so nothing is lost by skipping this one.
            _logger.LogWarning(ex, "Failed to write the EDDN heartbeat");
        }
    }

    public async Task<DateTimeOffset?> GetLastMessageUtcAsync()
    {
        var value = await _database.StringGetAsync(RedisKeys.EddnHeartbeat);
        return value.TryParse(out long unixMilliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds)
            : null;
    }
}