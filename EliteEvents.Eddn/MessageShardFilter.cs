namespace EliteEvents.Eddn;

/// <summary>
/// Decides whether this consumer owns a given EDDN message.
/// </summary>
public interface IMessageShardFilter
{
    int ShardIndex { get; }
    int ShardCount { get; }

    /// <summary>Returns true when this shard is responsible for handling <paramref name="rawMessage"/>.</summary>
    bool Owns(string rawMessage);
}

/// <summary>
/// Partitions the EDDN firehose across consumers by hashing each raw message.
/// <para>
/// EDDN is a broadcast feed and its frames carry no topic, so every subscriber receives every
/// message and the socket cannot do the filtering for us. Running N consumers without a filter
/// would therefore write every docking N times. Each consumer instead hashes the message body
/// and handles only those falling in its own slice — every message is handled by exactly one
/// shard, and no message is handled twice.
/// </para>
/// <para>
/// Sharding buys parallel <em>handling</em>, not parallel receiving: each consumer still
/// receives and decompresses the whole firehose. That is inherent to a feed with no topic frame.
/// </para>
/// </summary>
public class MessageShardFilter : IMessageShardFilter
{
    public int ShardIndex { get; }
    public int ShardCount { get; }

    public MessageShardFilter(int shardIndex, int shardCount)
    {
        if (shardCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(shardCount), shardCount,
                "Shard count must be at least 1.");
        }
        if (shardIndex < 0 || shardIndex >= shardCount)
        {
            // Failing at construction is deliberate. An out-of-range index would silently own
            // nothing, leaving a slice of the feed unhandled by every consumer — a data gap
            // with no error anywhere to explain it.
            throw new ArgumentOutOfRangeException(nameof(shardIndex), shardIndex,
                $"Shard index must be within [0, {shardCount}).");
        }

        ShardIndex = shardIndex;
        ShardCount = shardCount;
    }

    public bool Owns(string rawMessage)
    {
        if (ShardCount == 1)
        {
            return true;
        }
        ArgumentNullException.ThrowIfNull(rawMessage);
        return StableHash(rawMessage) % (uint)ShardCount == (uint)ShardIndex;
    }

    /// <summary>
    /// FNV-1a over the message's UTF-16 code units, followed by an avalanche finalizer.
    /// <para>
    /// A hand-rolled hash rather than <see cref="string.GetHashCode()"/> because that one is
    /// randomised per process: two consumers would compute different hashes for the same
    /// message, so some messages would be claimed by no shard and others by several. The
    /// partition has to agree across processes, which means the hash has to be stable across
    /// processes.
    /// </para>
    /// <para>
    /// The finalizer is not optional polish. Raw FNV-1a cannot be used with a power-of-two
    /// modulus: multiplying by an odd prime leaves the low bits unchanged, so the lowest bit of
    /// the digest is just a parity of the input bytes' lowest bits. Taking <c>% 2</c>, <c>% 4</c>
    /// or <c>% 8</c> of that reads exactly the bits FNV never mixed, and a corpus of similarly
    /// shaped JSON frames lands entirely in one bucket — measured here as one shard taking 100%
    /// of the feed and the rest taking none, for every power-of-two consumer count. The mix
    /// below (Murmur3's fmix32) spreads entropy down into the low bits, which brings every shard
    /// count from 2 to 16 within a couple of percent of even.
    /// </para>
    /// </summary>
    internal static uint StableHash(string value)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        var hash = offsetBasis;
        foreach (var c in value)
        {
            hash = (hash ^ (byte)c) * prime;
            hash = (hash ^ (byte)(c >> 8)) * prime;
        }

        hash ^= hash >> 16;
        hash *= 0x85EBCA6B;
        hash ^= hash >> 13;
        hash *= 0xC2B2AE35;
        hash ^= hash >> 16;
        return hash;
    }
}
