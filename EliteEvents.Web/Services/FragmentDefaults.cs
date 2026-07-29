namespace EliteEvents.Web.Services;

public static class FragmentDefaults
{
    /// <summary>Rows in the home-page leaderboard panel, matching the polling fragment.</summary>
    public const int LeaderboardSize = 25;

    /// <summary>Default and maximum <c>?limit=</c> on <c>/api/most-visited</c>, unchanged from the Next app.</summary>
    public const int ApiLeaderboardDefaultLimit = 25;

    public const int ApiLeaderboardMaxLimit = 100;
}
