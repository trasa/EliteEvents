namespace EliteEvents.Web.Services;

public static class UserMessages
{
    /// <summary>
    /// What a visitor sees when a Redis read fails. Deliberately generic: a StackExchange.Redis
    /// exception message carries the endpoint host, client name and library version, and the old
    /// Blazor pages printed it straight onto a public page. The detail goes to the log instead.
    /// </summary>
    public const string DataUnavailable = "Can't reach the data feed right now. Try again in a moment.";
}
