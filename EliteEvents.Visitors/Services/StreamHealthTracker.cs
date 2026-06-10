namespace EliteEvents.Visitors.Services;

/// <summary>
/// Tracks when the EDDN ZeroMQ stream last delivered a message. Shared (singleton)
/// between <see cref="EddnStreamReceiver"/> and the stream health check, since both
/// live in the same process.
/// </summary>
public class StreamHealthTracker
{
    // Seeded with start time so the silence threshold doubles as a startup grace window.
    private long _lastMessageTicks = DateTimeOffset.UtcNow.UtcTicks;

    public void RecordMessage() => Interlocked.Exchange(ref _lastMessageTicks, DateTimeOffset.UtcNow.UtcTicks);

    public DateTimeOffset LastMessageUtc => new(Interlocked.Read(ref _lastMessageTicks), TimeSpan.Zero);
}