using EliteEvents.Eddn.Config;
using EliteEvents.Eddn.Storage;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace EliteEvents.Web.Services;

/// <summary>
/// PINGs Redis on a timer and records the result in <see cref="RedisConnectivityState"/>, which
/// is what <see cref="RedisLivenessHealthCheck"/> reports on.
/// </summary>
/// <remarks>
/// <para>
/// The measuring is a hosted service rather than a side effect of the readiness probe on purpose.
/// Piggybacking on readiness would make liveness silently dependent on kubelet probing this pod
/// on schedule — the signal would go stale for reasons that have nothing to do with Redis, and it
/// would read "unreachable" on any host nobody is probing, including a developer's laptop. A loop
/// that does its own asking means the timestamp always describes Redis.
/// </para>
/// <para>
/// It lives in the web host rather than the storage layer for the same reason
/// <c>SearchIndexMaintenanceService</c> lives in Ingestion: the shared library holds the logic,
/// the host decides what runs on a schedule.
/// </para>
/// </remarks>
public sealed class RedisConnectivityMonitor : BackgroundService
{
    private readonly IConnectionMultiplexer _connection;
    private readonly RedisConnectivityState _state;
    private readonly RedisLivenessOptions _options;
    private readonly ILogger<RedisConnectivityMonitor> _logger;

    public RedisConnectivityMonitor(
        IConnectionMultiplexer connection,
        RedisConnectivityState state,
        IOptions<RedisLivenessOptions> options,
        ILogger<RedisConnectivityMonitor> logger)
    {
        _connection = connection;
        _state = state;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await ProbeAsync(stoppingToken);

            try
            {
                await Task.Delay(_options.ProbeInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ProbeAsync(CancellationToken stoppingToken)
    {
        // Every failure mode here is "Redis is not answering", which is the signal itself — so
        // nothing is rethrown. An exception escaping ExecuteAsync would stop the watchdog, and a
        // stopped watchdog reports a pod as stuck forever the moment its last success ages out.
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(_options.ProbeTimeout);

            await _connection.GetDatabase().PingAsync().WaitAsync(timeout.Token);
            _state.MarkReachable(DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: not a Redis failure, and not something to record either way.
        }
        catch (Exception ex)
        {
            var unreachableFor = _state.UnreachableFor(DateTimeOffset.UtcNow);
            _logger.LogWarning(ex,
                "Redis liveness probe failed; unreachable for {UnreachableSeconds:F0}s of {ThresholdSeconds:F0}s",
                unreachableFor.TotalSeconds, _options.UnreachableRestartThreshold.TotalSeconds);
        }
    }
}
