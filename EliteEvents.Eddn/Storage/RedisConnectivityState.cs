namespace EliteEvents.Eddn.Storage;

/// <summary>
/// The last moment Redis was confirmed reachable from this process, and the one judgement made
/// from it: whether the client has been broken long enough that only a restart will fix it.
/// <para>
/// This exists because of an outage on 2026-08-14. Both web pods sat <c>Running</c> with zero
/// restarts and <c>0/1 Ready</c> for hours: StackExchange.Redis' multiplexer had wedged — its
/// sockets to Redis were still ESTABLISHED and idle for 21.7 hours, and it was neither using
/// them nor opening replacements. Readiness correctly went false, which emptied the Service and
/// served 503s, but <c>/health/live</c> ran no checks at all, so nothing ever restarted the pod.
/// The site stayed down until a human noticed.
/// </para>
/// <para>
/// The distinction that matters is <em>sustained</em> failure. A pod retrying Redis for a few
/// seconds is healthy and must not be restarted — that leniency is the whole reason liveness was
/// check-free. A pod that has not reached Redis for a quarter of an hour is not retrying, it is
/// stuck.
/// </para>
/// </summary>
public class RedisConnectivityState
{
    private long _lastReachableTicks;

    /// <param name="startedUtc">
    /// Seeds the clock so a process that has never once reached Redis still trips eventually,
    /// rather than being immortal because it has no success to age from.
    /// </param>
    public RedisConnectivityState(DateTimeOffset startedUtc)
    {
        _lastReachableTicks = startedUtc.UtcTicks;
    }

    /// <summary>When Redis last answered — or process start, if it never has.</summary>
    public DateTimeOffset LastReachableUtc => new(Interlocked.Read(ref _lastReachableTicks), TimeSpan.Zero);

    /// <summary>
    /// Records a successful round trip to Redis.
    /// <para>
    /// Overlapping probes can write out of order, which can only ever age this value by less than
    /// one probe interval — orders of magnitude below the threshold it feeds, so the simple write
    /// is preferred over a compare-exchange loop.
    /// </para>
    /// </summary>
    public void MarkReachable(DateTimeOffset now) => Interlocked.Exchange(ref _lastReachableTicks, now.UtcTicks);

    /// <summary>How long Redis has been unreachable.</summary>
    public TimeSpan UnreachableFor(DateTimeOffset now) => now - LastReachableUtc;

    /// <summary>
    /// Whether the client is stuck rather than merely retrying. A non-positive
    /// <paramref name="threshold"/> disables the judgement entirely, which is how the watchdog is
    /// turned off without removing it from the pipeline.
    /// </summary>
    public bool IsStuck(DateTimeOffset now, TimeSpan threshold)
        => threshold > TimeSpan.Zero && UnreachableFor(now) > threshold;
}
