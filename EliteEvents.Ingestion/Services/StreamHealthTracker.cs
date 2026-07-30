namespace EliteEvents.Ingestion.Services;

/// <summary>
/// Tracks when the EDDN ZeroMQ stream last delivered a message. In-process and free to read,
/// so <see cref="EddnStreamReceiver"/> can evaluate its reconnect decision on every idle
/// receive without touching Redis. The same timestamp reaches the health checks — in this
/// process and in the web tier — as the throttled <c>heartbeat:eddn</c> key.
/// </summary>
public class StreamHealthTracker
{
    // Seeded with start time so the silence threshold doubles as a startup grace window.
    private long _lastMessageTicks = DateTimeOffset.UtcNow.UtcTicks;
    private long _messagesReceived;
    private long _messagesHandled;

    public void RecordMessage()
    {
        Interlocked.Exchange(ref _lastMessageTicks, DateTimeOffset.UtcNow.UtcTicks);
        Interlocked.Increment(ref _messagesReceived);
    }

    /// <summary>Counts a message this shard owned, as opposed to one it received and dropped.</summary>
    public void RecordHandled() => Interlocked.Increment(ref _messagesHandled);

    public DateTimeOffset LastMessageUtc => new(Interlocked.Read(ref _lastMessageTicks), TimeSpan.Zero);

    /// <summary>Frames received off the socket, including those belonging to other shards.</summary>
    public long MessagesReceived => Interlocked.Read(ref _messagesReceived);

    /// <summary>
    /// Frames this shard owned. Comparing this against <see cref="MessagesReceived"/> is how a
    /// skewed or misconfigured partition shows itself: with N shards the ratio should settle
    /// near 1/N, and a shard reporting zero handled against a healthy receive count is one whose
    /// slice of the feed is going nowhere.
    /// </summary>
    public long MessagesHandled => Interlocked.Read(ref _messagesHandled);
}