using EliteEvents.Eddn.Storage;
using EliteEvents.Web.Components.Pages;
using EliteEvents.Web.Components.Shared;
using EliteEvents.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http.HttpResults;

namespace EliteEvents.Web.Endpoints;

/// <summary>
/// HTML fragments for htmx swaps. Each one renders the same component the full page renders, so
/// a fragment and a fresh page load can never disagree, and every one of them is a GET.
/// </summary>
public static class FragmentEndpoints
{
    public static IEndpointRouteBuilder MapFragmentEndpoints(this IEndpointRouteBuilder app)
    {
        var fragments = app.MapGroup("/fragments");

        fragments.MapGet("/leaderboard", async (IDockingReader reader) =>
        {
            try
            {
                var systems = await reader.GetSystemVisitsAsync(FragmentDefaults.LeaderboardSize);
                return new RazorComponentResult<LeaderboardTable>(new { Systems = systems });
            }
            catch (Exception)
            {
                // The panel refreshes every 15s; a transient Redis failure should show a line of
                // text in the card, not an error page or a broken swap.
                return new RazorComponentResult<LeaderboardTable>(
                    new { ErrorMessage = UserMessages.DataUnavailable });
            }
        });

        fragments.MapGet("/system-search", async (HttpContext http, IDockingReader reader, ILoggerFactory loggers, string? q) =>
        {
            var query = q?.Trim();
            if (string.IsNullOrEmpty(query))
            {
                return new RazorComponentResult<SystemResults>(new { });
            }

            if (query.Length < SystemSearch.MinimumQueryLength)
            {
                return Fragment<SystemResults>(query,
                    $"Please enter at least {SystemSearch.MinimumQueryLength} characters to search.");
            }

            IReadOnlyList<string> matches;
            try
            {
                matches = await reader.GetMatchingSystemsAsync(query);
            }
            catch (Exception ex)
            {
                loggers.CreateLogger("Fragments.SystemSearch").LogWarning(ex, "System search failed for {Query}", query);
                return Fragment<SystemResults>(query, UserMessages.DataUnavailable);
            }

            // A single hit is a lookup, not a search: tell htmx to navigate, matching what the
            // full-page form does with a redirect.
            if (matches.Count == 1)
            {
                return Redirect(http, $"/system/{Uri.EscapeDataString(matches[0])}");
            }

            PushSearchUrl(http, "/system-search", query);
            return new RazorComponentResult<SystemResults>(new { Query = query, Systems = matches });
        });

        fragments.MapGet("/carrier-search", async (HttpContext http, IDockingReader reader, ILoggerFactory loggers, string? q) =>
        {
            var query = q?.Trim();
            if (string.IsNullOrEmpty(query))
            {
                return new RazorComponentResult<CarrierResults>(new { });
            }

            IReadOnlyList<string> matches;
            try
            {
                matches = await reader.GetMatchingCarriersAsync(query);
            }
            catch (Exception ex)
            {
                loggers.CreateLogger("Fragments.CarrierSearch").LogWarning(ex, "Carrier search failed for {Query}", query);
                return Fragment<CarrierResults>(query, UserMessages.DataUnavailable);
            }

            if (matches.Count == 1)
            {
                return Redirect(http, $"/carrier/{Uri.EscapeDataString(matches[0])}");
            }

            PushSearchUrl(http, "/carrier-search", query);
            return new RazorComponentResult<CarrierResults>(new { Query = query, Carriers = matches });
        });

        return app;
    }

    private static IResult Fragment<TComponent>(string query, string errorMessage) where TComponent : IComponent
        => new RazorComponentResult<TComponent>(new { Query = query, ErrorMessage = errorMessage });

    /// <summary>Client-side navigation for htmx; browsers without it followed a real redirect.</summary>
    private static IResult Redirect(HttpContext http, string location)
    {
        http.Response.Headers["HX-Redirect"] = location;
        return Results.NoContent();
    }

    /// <summary>
    /// Keeps the address bar on the page URL rather than the fragment URL, so a search stays
    /// bookmarkable and the back button works.
    /// </summary>
    private static void PushSearchUrl(HttpContext http, string path, string query)
        => http.Response.Headers["HX-Push-Url"] = $"{path}?q={Uri.EscapeDataString(query)}";
}
