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

    public void RecordMessage() => Interlocked.Exchange(ref _lastMessageTicks, DateTimeOffset.UtcNow.UtcTicks);

    public DateTimeOffset LastMessageUtc => new(Interlocked.Read(ref _lastMessageTicks), TimeSpan.Zero);
}