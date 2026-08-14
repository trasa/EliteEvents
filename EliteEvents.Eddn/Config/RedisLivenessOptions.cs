namespace EliteEvents.Eddn.Config;

/// <summary>
/// Tuning for the Redis liveness watchdog — the thing that restarts a pod whose Redis client has
/// stopped working and will not fix itself. See <c>RedisConnectivityState</c> for why readiness
/// alone was not enough.
/// </summary>
public class RedisLivenessOptions
{
    /// <summary>
    /// How often the watchdog PINGs Redis. This is the resolution of the whole signal, so it
    /// wants to be well under <see cref="UnreachableRestartThreshold"/> — several failed probes
    /// should be needed to trip it, not one unlucky one.
    /// </summary>
    public TimeSpan ProbeInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Bounds a single PING. Without it a wedged multiplexer produces a probe that never returns
    /// rather than one that fails, and a watchdog that never completes a cycle never trips —
    /// which is precisely the failure it exists to catch.
    /// </summary>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long Redis must be <em>continuously</em> unreachable before liveness fails and k8s
    /// restarts the pod.
    /// <para>
    /// The default is deliberately long. Liveness is otherwise check-free here on purpose: a pod
    /// briefly retrying Redis, or waiting out a quiet EDDN period, must not be restarted. Fifteen
    /// minutes is far past any reconnect a healthy client performs on its own, so reaching it
    /// means the client is not coming back. Set to <see cref="TimeSpan.Zero"/> to disable the
    /// watchdog and restore the old always-alive behaviour.
    /// </para>
    /// </summary>
    public TimeSpan UnreachableRestartThreshold { get; set; } = TimeSpan.FromMinutes(15);
}
