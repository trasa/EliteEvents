using Newtonsoft.Json;

namespace EliteEvents.Eddn.Storage;

/// <summary>
/// A single frame on the live ticker, published to <see cref="RedisKeys.EventsChannel"/> by the
/// ingestion service and consumed by the web tier's SSE endpoint.
/// <para>
/// The property names below are the wire contract. They were previously produced by an anonymous
/// object in the journal handler, so the lowercase names are pinned explicitly here rather than
/// left to depend on serializer settings.
/// </para>
/// </summary>
public sealed record LiveEvent
{
    [JsonProperty("type")]
    public required string Type { get; init; }

    [JsonProperty("system")]
    public string? System { get; init; }

    // Omitted when null so an fsdjump frame carries no station keys at all, matching the
    // anonymous object this record replaced.
    [JsonProperty("station", NullValueHandling = NullValueHandling.Ignore)]
    public string? Station { get; init; }

    [JsonProperty("stationType", NullValueHandling = NullValueHandling.Ignore)]
    public string? StationType { get; init; }

    /// <summary>Unix seconds, taken from the EDDN gateway timestamp rather than local time.</summary>
    [JsonProperty("ts")]
    public required long Ts { get; init; }

    public const string DockedType = "docked";
    public const string FsdJumpType = "fsdjump";

    public static LiveEvent Docked(string? system, string? station, string? stationType, DateTimeOffset timestamp)
        => new()
        {
            Type = DockedType,
            System = system,
            Station = station,
            StationType = stationType,
            Ts = timestamp.ToUnixTimeSeconds(),
        };

    public static LiveEvent FsdJump(string? system, DateTimeOffset timestamp)
        => new()
        {
            Type = FsdJumpType,
            System = system,
            Ts = timestamp.ToUnixTimeSeconds(),
        };
}
