namespace EliteEvents.Web.Services;

public static class TimeFormat
{
    /// <summary>Coarse "how long ago" wording, falling back to a date once it stops being useful.</summary>
    public static string Ago(DateTimeOffset timestamp)
    {
        var elapsed = DateTimeOffset.UtcNow - timestamp;

        return elapsed switch
        {
            { TotalMinutes: < 1 } => "Just now",
            { TotalMinutes: < 60 } => $"{(int)elapsed.TotalMinutes}m ago",
            { TotalHours: < 24 } => $"{(int)elapsed.TotalHours}h ago",
            { TotalDays: < 7 } => $"{(int)elapsed.TotalDays}d ago",
            _ => timestamp.ToString("MMM dd, yyyy")
        };
    }
}
