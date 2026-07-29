using StackExchange.Redis;

namespace EliteEvents.Eddn.Storage;

/// <summary>
/// The single definition of the Redis schema: every key format, hash field, scan pattern,
/// and the name normalization rules that go with them.
/// <para>
/// This used to be duplicated across the Blazor app's services and the Next.js query layer,
/// which is how the two front ends could silently drift apart. Readers and writers now build
/// every key through here, so a schema change is a one-file change.
/// </para>
/// </summary>
public static class RedisKeys
{
    /// <summary>
    /// Rolling window applied to system and carrier data. Refreshed on every write, so a
    /// system stops being tracked 30 days after EDDN last mentioned it.
    /// </summary>
    public static readonly TimeSpan DataExpiration = TimeSpan.FromDays(30);

    /// <summary>How long <see cref="SystemCountCache"/> stays warm.</summary>
    public static readonly TimeSpan SystemCountCacheDuration = TimeSpan.FromSeconds(60);

    /// <summary>Pub/sub channel carrying the live event ticker feed.</summary>
    public static readonly RedisChannel EventsChannel = RedisChannel.Literal("eddn:events");

    // ---- normalization -------------------------------------------------------------------
    //
    // System names and carrier IDs are upper-cased so lookups are case-insensitive; station
    // names are stored verbatim, because they are only ever read back out of an index rather
    // than being used to build a lookup key from user input.

    public static string NormalizeSystem(string systemName) => systemName.ToUpperInvariant();

    public static string NormalizeCarrier(string carrierId) => carrierId.ToUpperInvariant();

    /// <summary>Search input also gets trimmed, since it comes straight from a text box.</summary>
    public static string NormalizeQuery(string searchQuery) => searchQuery.ToUpperInvariant().Trim();

    // ---- system keys ---------------------------------------------------------------------

    /// <summary>Hash of <see cref="StationCountField"/>, <see cref="StationTypeField"/>, <see cref="StationLastSeenField"/>.</summary>
    public static string Station(string normalizedSystem, string stationName)
        => $"system:{normalizedSystem}:station:{stationName}";

    /// <summary>Sorted set indexing a system's stations by last-visit unix time.</summary>
    public static string SystemStations(string normalizedSystem)
        => $"system:{normalizedSystem}:stations";

    public const string StationCountField = "count";
    public const string StationTypeField = "type";
    public const string StationLastSeenField = "last_seen";

    // ---- carrier keys --------------------------------------------------------------------

    /// <summary>String counter of dockings on a single day.</summary>
    public static string CarrierDaily(string normalizedCarrier, string date)
        => $"carrier:{normalizedCarrier}:daily:{date}";

    /// <summary>Sorted set of a carrier's active dates, scored by unix time.</summary>
    public static string CarrierDays(string normalizedCarrier)
        => $"carrier:{normalizedCarrier}:days";

    /// <summary>Date component of <see cref="CarrierDaily"/>, and the member format of <see cref="CarrierDays"/>.</summary>
    public const string DateFormat = "yyyy-MM-dd";

    // ---- global keys ---------------------------------------------------------------------

    /// <summary>Global most-visited leaderboard. Unlike everything else, expires weekly.</summary>
    public const string SystemVisits = "systems:visits";

    /// <summary>Cached total system count, to avoid a full SCAN per page render.</summary>
    public const string SystemCountCache = "cache:system:count";

    /// <summary>
    /// Unix-millisecond timestamp of the last EDDN message the ingestion service received.
    /// The receiver used to share this with the health check through an in-process field; once
    /// ingestion and the web tier are separate containers the signal has to travel through
    /// Redis for the web tier — and any external uptime monitor — to see it.
    /// </summary>
    public const string EddnHeartbeat = "heartbeat:eddn";

    /// <summary>
    /// Minimum spacing between heartbeat writes. EDDN delivers several messages a second and
    /// the readers only care about resolution of minutes, so writing every message would be
    /// pure round-trip waste.
    /// </summary>
    public static readonly TimeSpan HeartbeatWriteInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// TTL on the heartbeat, comfortably longer than any silence threshold, so a torn-down
    /// ingestion service doesn't leave a stale timestamp behind forever.
    /// </summary>
    public static readonly TimeSpan HeartbeatExpiration = TimeSpan.FromHours(1);

    // ---- scan patterns -------------------------------------------------------------------

    /// <summary>Matches one key per system that has any station activity.</summary>
    public const string AllSystemStationsPattern = "system:*:stations";

    /// <summary>
    /// Substring search over system keys. Note this matches the station segment too, so a query
    /// can hit a system by way of one of its station names — long-standing behavior, preserved.
    /// </summary>
    public static string SystemSearchPattern(string normalizedQuery) => $"system:*{normalizedQuery}*";

    public static string CarrierSearchPattern(string normalizedQuery) => $"carrier:*{normalizedQuery}*";

    /// <summary>Real data keys, deliberately excluding <c>cache:*</c>, for the health check.</summary>
    public static readonly string[] DataKeyPatterns = ["system:*", "carrier:*"];

    /// <summary>
    /// Pulls the system or carrier name out of any <c>system:{NAME}:...</c> / <c>carrier:{ID}:...</c>
    /// key. Returns null when the key has no name segment.
    /// </summary>
    public static string? ExtractName(string key)
    {
        var parts = key.Split(':');
        return parts.Length > 1 ? parts[1] : null;
    }
}
