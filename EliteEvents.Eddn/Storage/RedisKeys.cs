using System.Text;
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

    // ---- search index --------------------------------------------------------------------
    //
    // Search used to be a SCAN of the whole keyspace with a *glob* pattern, which is O(keyspace)
    // per keystroke — and the keyspace is dominated by per-station hashes, so it was orders of
    // magnitude larger than the set of things actually being searched. These two sorted sets hold
    // one member per searchable name and nothing else.
    //
    // Every member is stored at score 0 on purpose: ZRANGEBYLEX only has defined behaviour when
    // all members share a score, and that lexicographic ordering is what makes a prefix lookup
    // O(log N + M) instead of O(N). The cost of that choice is that the index cannot also carry a
    // last-seen timestamp, so it cannot be pruned by score — SearchIndexMaintainer rebuilds it
    // from the keyspace instead. See that class for why this is a feature rather than a
    // workaround.

    /// <summary>Lex-ordered set of system names that have station activity. All scores are 0.</summary>
    public const string SystemIndex = "index:systems";

    /// <summary>Lex-ordered set of known carrier IDs. All scores are 0.</summary>
    public const string CarrierIndex = "index:carriers";

    /// <summary>Score every index member gets, so <c>ZRANGEBYLEX</c> is well defined.</summary>
    public const double IndexScore = 0;

    /// <summary>
    /// Inclusive lower bound of a <c>ZRANGEBYLEX</c> prefix lookup — the prefix itself.
    /// </summary>
    public static RedisValue LexPrefixMin(string normalizedPrefix) => normalizedPrefix;

    /// <summary>
    /// Inclusive upper bound of a prefix lookup: the prefix with a trailing <c>0xFF</c> byte.
    /// <para>
    /// This is deliberately built as raw bytes rather than by appending a char. Redis compares
    /// members as byte strings, and no valid UTF-8 sequence contains <c>0xFF</c>, so this sorts
    /// after every possible continuation of the prefix. Appending <c>'￿'</c> instead would
    /// encode to <c>EF BF BF</c> and silently miss any name whose next byte is higher.
    /// </para>
    /// </summary>
    public static RedisValue LexPrefixMax(string normalizedPrefix)
    {
        var prefix = Encoding.UTF8.GetBytes(normalizedPrefix);
        var bound = new byte[prefix.Length + 1];
        prefix.CopyTo(bound, 0);
        bound[^1] = 0xFF;
        return bound;
    }

    /// <summary>
    /// <c>ZSCAN</c> MATCH pattern for a substring search within an index. Used as the fallback
    /// when a prefix lookup doesn't fill the page — it is O(index) on the server, but the index
    /// holds one member per system rather than one key per station, and matching happens
    /// server-side so only hits cross the wire.
    /// </summary>
    public static string IndexMatchPattern(string normalizedQuery) => $"*{EscapeGlob(normalizedQuery)}*";

    /// <summary>
    /// Escapes the glob metacharacters Redis honours in a MATCH pattern. Search text comes
    /// straight from a text box, so an unescaped <c>*</c> or <c>[</c> would turn a literal query
    /// into a wildcard — at best surprising results, at worst an unbounded scan from a one-key
    /// query. The old keyspace-glob search interpolated the query raw and had this same hole.
    /// </summary>
    public static string EscapeGlob(string value)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '*' or '?' or '[' or ']' or '\\')
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.ToString();
    }

    // ---- scan patterns -------------------------------------------------------------------

    /// <summary>Matches one key per system that has any station activity.</summary>
    public const string AllSystemStationsPattern = "system:*:stations";

    /// <summary>Matches one key per carrier that has any recorded activity.</summary>
    public const string AllCarrierDaysPattern = "carrier:*:days";

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
