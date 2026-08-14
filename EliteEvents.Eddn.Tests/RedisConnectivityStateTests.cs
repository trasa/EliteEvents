using EliteEvents.Eddn.Storage;

namespace EliteEvents.Eddn.Tests;

/// <summary>
/// The web tier's liveness probe answered unconditionally until a wedged Redis client left both
/// pods Running, never restarted, and permanently NotReady — the site was down for hours because
/// nothing could conclude the pods were unrecoverable. This state object is that conclusion, and
/// both of its mistakes are expensive: tripping early restarts pods that were only retrying,
/// tripping never restores the outage it was written for. The threshold behaviour is therefore
/// pinned against explicit instants rather than against a live clock.
/// </summary>
public class RedisConnectivityStateTests
{
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(15);

    private static DateTimeOffset At(int hour, int minute, int second = 0)
        => new(2026, 8, 14, hour, minute, second, TimeSpan.Zero);

    [Fact]
    public void A_fresh_process_ages_from_its_start_time()
    {
        // A process that has never once reached Redis still has to be judgeable, or a pod that
        // came up against a dead Redis and wedged immediately would be immortal.
        var state = new RedisConnectivityState(At(12, 00));

        Assert.False(state.IsStuck(At(12, 14, 59), Threshold));
        Assert.True(state.IsStuck(At(12, 15, 01), Threshold));
    }

    [Fact]
    public void A_success_resets_the_clock()
    {
        var state = new RedisConnectivityState(At(12, 00));
        state.MarkReachable(At(12, 10));

        // 12:16 is past the threshold measured from start, but only six minutes from the success.
        Assert.False(state.IsStuck(At(12, 16), Threshold));
        Assert.True(state.IsStuck(At(12, 25, 01), Threshold));
    }

    [Fact]
    public void A_retrying_client_that_keeps_succeeding_never_trips()
    {
        // The leniency that justified a check-free liveness probe in the first place: brief
        // failures between successes must not accumulate toward a restart.
        var state = new RedisConnectivityState(At(12, 00));

        foreach (var minute in Enumerable.Range(1, 60))
        {
            state.MarkReachable(At(12, 00).AddMinutes(minute));
            Assert.False(state.IsStuck(At(12, 00).AddMinutes(minute).AddMinutes(14), Threshold));
        }
    }

    [Fact]
    public void The_threshold_is_exclusive_at_the_boundary()
    {
        var state = new RedisConnectivityState(At(12, 00));

        // Exactly at the threshold is not yet stuck: the probe that would have refreshed it is
        // allowed to be the one landing on this instant.
        Assert.False(state.IsStuck(At(12, 15), Threshold));
    }

    [Fact]
    public void A_non_positive_threshold_disables_the_watchdog()
    {
        // This is how the watchdog is turned off in an emergency — by configuration, without
        // shipping a build that removes it from the pipeline.
        var state = new RedisConnectivityState(At(12, 00));

        Assert.False(state.IsStuck(At(23, 59), TimeSpan.Zero));
        Assert.False(state.IsStuck(At(23, 59), TimeSpan.FromMinutes(-5)));
    }

    [Fact]
    public void Unreachable_duration_is_measured_from_the_last_success()
    {
        var state = new RedisConnectivityState(At(12, 00));
        state.MarkReachable(At(12, 30));

        Assert.Equal(TimeSpan.FromMinutes(5), state.UnreachableFor(At(12, 35)));
        Assert.Equal(At(12, 30), state.LastReachableUtc);
    }
}
