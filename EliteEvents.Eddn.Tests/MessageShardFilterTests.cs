using EliteEvents.Eddn;

namespace EliteEvents.Eddn.Tests;

/// <summary>
/// The shard filter is the only thing standing between a multi-consumer FeedListener and
/// corrupted counts. EDDN broadcasts every message to every subscriber, so if the partition has
/// a gap those events are silently lost, and if it overlaps every affected docking is counted
/// more than once. Neither failure raises an error anywhere — the site just shows wrong numbers.
/// These tests are therefore about the partition itself, not about the hash function.
/// </summary>
public class MessageShardFilterTests
{
    private static string Message(int i) =>
        "{\"$schemaRef\":\"https://eddn.edcd.io/schemas/journal/1\","
        + "\"header\":{\"uploaderID\":\"cmdr" + i + "\"},"
        + "\"message\":{\"event\":\"Docked\",\"StarSystem\":\"SOL " + i + "\"}}";

    private static string[] Corpus(int count) =>
        Enumerable.Range(0, count).Select(Message).ToArray();

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void EveryMessageIsOwnedByExactlyOneShard(int shardCount)
    {
        var filters = Enumerable.Range(0, shardCount)
            .Select(i => new MessageShardFilter(i, shardCount))
            .ToArray();

        foreach (var message in Corpus(5_000))
        {
            var owners = filters.Count(f => f.Owns(message));
            Assert.True(owners == 1,
                $"message owned by {owners} of {shardCount} shards; expected exactly 1");
        }
    }

    [Fact]
    public void SingleShardOwnsEverything()
    {
        var filter = new MessageShardFilter(0, 1);
        Assert.All(Corpus(1_000), m => Assert.True(filter.Owns(m)));
    }

    /// <summary>
    /// Shards run in separate pods, so the hash has to agree across processes.
    /// <c>string.GetHashCode()</c> does not — it is randomised per process, which would give
    /// each pod a different partition of the same feed and produce both gaps and overlaps.
    /// <para>
    /// These are pinned literals, in the same spirit as the <c>RedisKeys</c> wire-format tests:
    /// they are FNV-1a over UTF-16 code units, so they deliberately do not match the published
    /// FNV byte-string vectors. The point is not which hash it is but that it never changes —
    /// swapping the algorithm during a rolling update would repartition the feed mid-flight,
    /// and this test is what makes that a build failure instead of a data one.
    /// </para>
    /// </summary>
    [Fact]
    public void HashIsStableAcrossProcesses()
    {
        Assert.Equal(0xAB3E7C0Bu, MessageShardFilter.StableHash(""));
        Assert.Equal(0x91E9DA27u, MessageShardFilter.StableHash("a"));
        Assert.Equal(0xCFCADD06u, MessageShardFilter.StableHash("foobar"));
    }

    [Fact]
    public void OwnershipIsDeterministic()
    {
        var message = Message(42);
        var first = new MessageShardFilter(1, 4);
        var second = new MessageShardFilter(1, 4);

        Assert.Equal(first.Owns(message), second.Owns(message));
        Assert.All(Enumerable.Range(0, 100), _ => Assert.Equal(first.Owns(message), first.Owns(message)));
    }

    /// <summary>
    /// Regression, and the reason the hash has an avalanche step.
    /// <para>
    /// Raw FNV-1a put every message into a single bucket for every power-of-two shard count:
    /// multiplying by an odd prime leaves the low bits untouched, so <c>% 2</c>, <c>% 4</c> and
    /// <c>% 8</c> read bits the hash never mixed. The partition was still technically
    /// exactly-once — which is why the correctness tests above passed — but one shard did all
    /// the work and the others silently idled. Powers of two are the counts anyone would
    /// actually pick, so this is tested at every one of them up to the CRD's maximum.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(16)]
    public void EveryShardGetsAWorkingShareOfTheFeed(int shardCount)
    {
        var corpus = Corpus(20_000);
        var expected = 1.0 / shardCount;

        for (var i = 0; i < shardCount; i++)
        {
            var filter = new MessageShardFilter(i, shardCount);
            var share = corpus.Count(filter.Owns) / (double)corpus.Length;

            Assert.True(share > expected * 0.75 && share < expected * 1.25,
                $"shard {i} of {shardCount} took {share:P2} of the feed; expected roughly {expected:P2}");
        }
    }

    [Theory]
    [InlineData(-1, 4)]
    [InlineData(4, 4)]
    [InlineData(5, 4)]
    public void OutOfRangeShardIndexThrows(int shardIndex, int shardCount)
    {
        // Loud at construction rather than quiet at runtime: a filter that owns nothing would
        // drop its whole slice of the feed with no error to explain the missing data.
        Assert.Throws<ArgumentOutOfRangeException>(() => new MessageShardFilter(shardIndex, shardCount));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveShardCountThrows(int shardCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MessageShardFilter(0, shardCount));
    }
}
