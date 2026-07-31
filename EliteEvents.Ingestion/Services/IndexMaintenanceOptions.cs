namespace EliteEvents.Ingestion.Services;

/// <summary>
/// Controls the in-process half of search-index upkeep. Bound from the <c>IndexMaintenance</c>
/// configuration section; the FeedListener controller sets these on the shared ConfigMap.
/// </summary>
public class IndexMaintenanceOptions
{
    public const string SectionName = "IndexMaintenance";

    /// <summary>
    /// Whether this process runs rebuilds on a timer.
    /// <para>
    /// Set false when something outside the process owns the schedule — in production that is the
    /// CronJob the FeedListener controller reconciles from <c>spec.indexMaintenance.schedule</c>.
    /// It does not disable the startup rebuild: that pass is not a scheduled rebuild but the
    /// recovery path for an index that does not exist yet, and nothing external can cover the
    /// window between a pod starting and the next cron tick.
    /// </para>
    /// </summary>
    public bool Periodic { get; set; } = true;

    /// <summary>
    /// Gap between rebuilds when <see cref="Periodic"/> is set. Only staleness under the 30-day
    /// TTL depends on this; it is far more frequent than correctness needs because a rebuild is
    /// also how an evicted index comes back.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);
}
