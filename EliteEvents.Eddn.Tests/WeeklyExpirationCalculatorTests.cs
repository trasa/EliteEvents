using EliteEvents.Eddn.Storage;

namespace EliteEvents.Eddn.Tests;

/// <summary>
/// The most-visited leaderboard is the one key that does not use a rolling TTL — it expires at
/// Elite Dangerous's weekly server tick, 07:30 UTC on Thursday. Getting this wrong by an hour
/// or a day is invisible until the leaderboard resets on the wrong day, so the cron expression
/// is pinned against explicit dates rather than against itself.
/// </summary>
public class WeeklyExpirationCalculatorTests
{
    private readonly WeeklyExpirationCalculator _calculator = new();

    private static DateTime Utc(int year, int month, int day, int hour, int minute)
        => new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    [Fact]
    public void From_a_monday_the_next_reset_is_that_week_thursday()
    {
        // Monday 2026-07-27 -> Thursday 2026-07-30 07:30 UTC.
        Assert.Equal(Utc(2026, 7, 30, 7, 30), _calculator.GetNextExpirationUtc(Utc(2026, 7, 27, 12, 0)));
    }

    [Fact]
    public void Earlier_on_reset_day_the_reset_is_still_today()
    {
        Assert.Equal(Utc(2026, 7, 30, 7, 30), _calculator.GetNextExpirationUtc(Utc(2026, 7, 30, 0, 0)));
        Assert.Equal(Utc(2026, 7, 30, 7, 30), _calculator.GetNextExpirationUtc(Utc(2026, 7, 30, 7, 29)));
    }

    [Fact]
    public void After_the_reset_it_rolls_a_full_week()
    {
        // 07:31 on reset day has missed it; the next one is seven days out.
        Assert.Equal(Utc(2026, 8, 6, 7, 30), _calculator.GetNextExpirationUtc(Utc(2026, 7, 30, 7, 31)));
        Assert.Equal(Utc(2026, 8, 6, 7, 30), _calculator.GetNextExpirationUtc(Utc(2026, 7, 30, 23, 59)));
    }

    [Fact]
    public void The_reset_instant_itself_yields_the_following_week()
    {
        // Cronos's GetNextOccurrence excludes the starting instant by default, so a write landing
        // exactly at 07:30 gets a full week of TTL rather than an expiration of now. That is the
        // behaviour we want — a zero TTL would delete the key the writer is building — but it is
        // a default, not a choice made in our code, so it is worth holding still.
        Assert.Equal(Utc(2026, 8, 6, 7, 30), _calculator.GetNextExpirationUtc(Utc(2026, 7, 30, 7, 30)));
    }

    [Fact]
    public void Every_result_is_a_thursday_at_0730_utc()
    {
        // Walk a full year in six-hour steps: whatever the input, the answer is always the next
        // Thursday 07:30, is always in the future, and is never more than a week away.
        var cursor = Utc(2026, 1, 1, 0, 0);
        var end = Utc(2027, 1, 1, 0, 0);

        while (cursor < end)
        {
            var next = _calculator.GetNextExpirationUtc(cursor);

            Assert.NotNull(next);
            Assert.Equal(DayOfWeek.Thursday, next!.Value.DayOfWeek);
            Assert.Equal(7, next.Value.Hour);
            Assert.Equal(30, next.Value.Minute);
            Assert.Equal(0, next.Value.Second);
            Assert.True(next > cursor, $"expiration {next} must be after {cursor}");
            Assert.True(next - cursor <= TimeSpan.FromDays(7), $"expiration {next} is more than a week after {cursor}");

            cursor = cursor.AddHours(6);
        }
    }

    [Fact]
    public void Result_is_expressed_in_utc()
    {
        // The caller subtracts DateTime.UtcNow from this to get a TTL. A result tagged Local or
        // Unspecified would produce a TTL wrong by the host's offset — and be correct on a UTC
        // container while failing on a developer's machine.
        var next = _calculator.GetNextExpirationUtc(Utc(2026, 7, 27, 12, 0));

        Assert.NotNull(next);
        Assert.Equal(DateTimeKind.Utc, next!.Value.Kind);
    }

    [Fact]
    public void Crossing_a_month_and_a_year_boundary_still_lands_on_thursday()
    {
        // Tuesday 2026-12-29 -> Thursday 2026-12-31.
        Assert.Equal(Utc(2026, 12, 31, 7, 30), _calculator.GetNextExpirationUtc(Utc(2026, 12, 29, 12, 0)));

        // Thursday 2026-12-31, after the reset -> Thursday 2027-01-07.
        Assert.Equal(Utc(2027, 1, 7, 7, 30), _calculator.GetNextExpirationUtc(Utc(2026, 12, 31, 8, 0)));
    }

    [Fact]
    public void Reset_is_unaffected_by_daylight_saving_transitions()
    {
        // The schedule is evaluated in UTC, which has no DST. Around the US and EU clock changes
        // in March 2026 the reset stays at 07:30 UTC rather than drifting an hour — which is the
        // whole reason TimeZoneInfo.Utc is passed explicitly.
        Assert.Equal(Utc(2026, 3, 12, 7, 30), _calculator.GetNextExpirationUtc(Utc(2026, 3, 9, 12, 0)));
        Assert.Equal(Utc(2026, 4, 2, 7, 30), _calculator.GetNextExpirationUtc(Utc(2026, 3, 30, 12, 0)));
    }
}
