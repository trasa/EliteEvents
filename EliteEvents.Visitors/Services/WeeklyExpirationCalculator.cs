using Cronos;

namespace EliteEvents.Visitors.Services;

public class WeeklyExpirationCalculator
{
    // every thursday at 0730 (utc)
    private const string WeeklyResetCron = "30 7 * * 4";
    private static readonly CronExpression WeeklyReset = CronExpression.Parse(WeeklyResetCron);

    public DateTime? GetNextExpirationUtc(DateTime utcNow) => WeeklyReset.GetNextOccurrence(utcNow, TimeZoneInfo.Utc);
}
