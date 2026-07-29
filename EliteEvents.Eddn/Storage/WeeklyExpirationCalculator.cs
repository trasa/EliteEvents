using Cronos;

namespace EliteEvents.Eddn.Storage;

/// <summary>
/// Works out when the most-visited leaderboard resets. Elite Dangerous does its weekly
/// server maintenance at 07:30 UTC on Thursday, so the leaderboard is scoped to that cycle
/// rather than to a rolling window like the rest of the data.
/// </summary>
public class WeeklyExpirationCalculator
{
    // every thursday at 0730 (utc)
    private const string WeeklyResetCron = "30 7 * * 4";
    private static readonly CronExpression WeeklyReset = CronExpression.Parse(WeeklyResetCron);

    public DateTime? GetNextExpirationUtc(DateTime utcNow) => WeeklyReset.GetNextOccurrence(utcNow, TimeZoneInfo.Utc);
}
