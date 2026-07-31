using EliteEvents.Eddn.Config;
using EliteEvents.Eddn.Storage;
using Microsoft.Extensions.Options;

namespace EliteEvents.Ingestion.Services;

/// <summary>
/// Runs <see cref="ISearchIndexMaintainer"/> in-process. The scheduling lives here rather than
/// in the storage layer because <c>EliteEvents.Eddn</c> is a plain class library with no hosting
/// dependency, and because this is squarely a writer's job — the web tier must never run it.
/// <para>
/// Only shard 0 does any of it. A rebuild reconciles the whole system and carrier keyspace
/// against the whole index; it is not partitioned the way the feed is, so every shard running it
/// means N identical full passes producing one identical result. Shard 0 always exists — the
/// partition is 0..consumers-1 — so gating on it needs no election and no extra state.
/// </para>
/// <para>
/// The first pass happens at startup, which is what backfills an index that does not exist yet
/// and what restores one that Redis evicted. It is scheduled like any other pass rather than
/// blocking startup: readiness already gates on Redis, and a rebuild that fails must not stop the
/// host from ingesting. That startup pass runs even when
/// <see cref="IndexMaintenanceOptions.Periodic"/> is off and a CronJob owns the schedule —
/// a cron tick cannot cover the window between a deploy and its own first firing, which is
/// exactly when the index is most likely to be missing.
/// </para>
/// </summary>
public class SearchIndexMaintenanceService : BackgroundService
{
    /// <summary>
    /// Grace period before the first pass. Redis is configured with
    /// <c>AbortOnConnectFail = false</c>, so at startup the connection is very likely still being
    /// established and an immediate scan would just fail.
    /// </summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Retry gap used until the first rebuild succeeds. Until then the index may not exist at all
    /// — which is the state of production on the deploy that introduces it — and while it does
    /// not, search returns nothing and the system count reads zero. Waiting a full
    /// <see cref="IndexMaintenanceOptions.Interval"/> to retry that is far too long; once a
    /// rebuild has succeeded, a failure is merely staleness and the normal interval applies.
    /// </summary>
    private static readonly TimeSpan RetryUntilFirstSuccess = TimeSpan.FromSeconds(15);

    private readonly ILogger<SearchIndexMaintenanceService> _logger;
    private readonly ISearchIndexMaintainer _maintainer;
    private readonly IndexMaintenanceOptions _options;
    private readonly EddnOptions _eddn;
    private bool _hasSucceeded;

    public SearchIndexMaintenanceService(
        ILogger<SearchIndexMaintenanceService> logger,
        ISearchIndexMaintainer maintainer,
        IOptions<IndexMaintenanceOptions> options,
        IOptions<EddnOptions> eddn)
    {
        _logger = logger;
        _maintainer = maintainer;
        _options = options.Value;
        _eddn = eddn.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_eddn.ShardIndex != 0)
        {
            _logger.LogInformation(
                "Shard {ShardIndex} does not maintain the search indexes; shard 0 owns them",
                _eddn.ShardIndex);
            return;
        }

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);

            // Keep trying on the short interval until one pass gets through, then either settle
            // into the periodic cadence or hand off to whatever owns the schedule.
            while (!_hasSucceeded)
            {
                await RebuildAsync(stoppingToken);
                if (!_hasSucceeded)
                {
                    await Task.Delay(RetryUntilFirstSuccess, stoppingToken);
                }
            }

            if (!_options.Periodic)
            {
                _logger.LogInformation(
                    "Startup index rebuild complete; periodic rebuilds are owned externally");
                return;
            }

            using var timer = new PeriodicTimer(_options.Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RebuildAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    private async Task RebuildAsync(CancellationToken cancellationToken)
    {
        // Failures are swallowed on purpose. A stale index degrades search; an exception escaping
        // ExecuteAsync would stop the whole host under the default
        // BackgroundServiceExceptionBehavior.StopHost and take ingestion down with it — the same
        // trap the EDDN receiver already guards against.
        try
        {
            await _maintainer.RebuildSystemIndexAsync(cancellationToken);
            await _maintainer.RebuildCarrierIndexAsync(cancellationToken);
            _hasSucceeded = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var retryIn = _hasSucceeded ? _options.Interval : RetryUntilFirstSuccess;
            _logger.LogWarning(ex, "Search index rebuild failed; will retry in {Interval}", retryIn);
        }
    }
}
