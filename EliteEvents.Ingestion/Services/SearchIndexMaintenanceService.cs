using EliteEvents.Eddn.Storage;

namespace EliteEvents.Ingestion.Services;

/// <summary>
/// Runs <see cref="ISearchIndexMaintainer"/> on a schedule. The scheduling lives here rather than
/// in the storage layer because <c>EliteEvents.Eddn</c> is a plain class library with no hosting
/// dependency, and because this is squarely a writer's job — the web tier must never run it.
/// <para>
/// The first pass happens at startup, which is what backfills an index that does not exist yet
/// and what restores one that Redis evicted. It is scheduled like any other pass rather than
/// blocking startup: readiness already gates on Redis, and a rebuild that fails must not stop the
/// host from ingesting.
/// </para>
/// </summary>
public class SearchIndexMaintenanceService : BackgroundService
{
    /// <summary>
    /// Gap between rebuilds. The index only goes stale as data ages out under a 30-day TTL, so
    /// hourly is far more often than correctness needs; it is this frequent because a rebuild is
    /// also the recovery path for an evicted index, and an hour is how long search would stay
    /// broken in that case.
    /// </summary>
    private static readonly TimeSpan RebuildInterval = TimeSpan.FromHours(1);

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
    /// <see cref="RebuildInterval"/> to retry that is far too long; once a rebuild has succeeded,
    /// a failure is merely staleness and the normal interval applies.
    /// </summary>
    private static readonly TimeSpan RetryUntilFirstSuccess = TimeSpan.FromSeconds(15);

    private readonly ILogger<SearchIndexMaintenanceService> _logger;
    private readonly ISearchIndexMaintainer _maintainer;
    private bool _hasSucceeded;

    public SearchIndexMaintenanceService(
        ILogger<SearchIndexMaintenanceService> logger, ISearchIndexMaintainer maintainer)
    {
        _logger = logger;
        _maintainer = maintainer;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);

            // Keep trying on the short interval until one pass gets through, then settle into the
            // hourly cadence.
            while (!_hasSucceeded)
            {
                await RebuildAsync(stoppingToken);
                if (!_hasSucceeded)
                {
                    await Task.Delay(RetryUntilFirstSuccess, stoppingToken);
                }
            }

            using var timer = new PeriodicTimer(RebuildInterval);
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
            var retryIn = _hasSucceeded ? RebuildInterval : RetryUntilFirstSuccess;
            _logger.LogWarning(ex, "Search index rebuild failed; will retry in {Interval}", retryIn);
        }
    }
}
