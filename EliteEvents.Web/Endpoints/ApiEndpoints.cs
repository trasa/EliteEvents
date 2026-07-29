using EliteEvents.Eddn.Storage;
using EliteEvents.Web.Services;

namespace EliteEvents.Web.Endpoints;

/// <summary>
/// The JSON API the Next dashboard exposed at elite.meancat.com. The response shapes, status
/// codes and error body are reproduced exactly so anything already consuming them keeps working
/// after the cutover; only the implementation moved from TypeScript to C#.
/// </summary>
public static class ApiEndpoints
{
    private const string RedisUnavailable = "redis unavailable";

    public static IEndpointRouteBuilder MapApiEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/most-visited", async (IDockingReader reader, ILoggerFactory loggers, int? limit) =>
        {
            var take = Math.Min(limit is > 0 ? limit.Value : FragmentDefaults.ApiLeaderboardDefaultLimit,
                FragmentDefaults.ApiLeaderboardMaxLimit);
            try
            {
                var systems = await reader.GetSystemVisitsAsync(take);
                return Results.Ok(new MostVisitedResponse(
                    systems.Select(s => new VisitedSystem(s.SystemName, s.VisitCount)).ToList()));
            }
            catch (Exception ex)
            {
                loggers.CreateLogger("Api.MostVisited").LogWarning(ex, "most-visited query failed");
                return Unavailable();
            }
        });

        api.MapGet("/system/{name}", async (IDockingReader reader, ILoggerFactory loggers, string name) =>
        {
            try
            {
                var stations = await reader.GetSystemDockingAsync(name);
                return Results.Ok(new SystemResponse(
                    RedisKeys.NormalizeSystem(name),
                    stations.Select(s => new StationDocking(
                        s.StationName,
                        string.IsNullOrEmpty(s.StationType) ? "Unknown" : s.StationType,
                        s.DockingCount,
                        s.LastSeen.ToUnixTimeSeconds())).ToList()));
            }
            catch (Exception ex)
            {
                loggers.CreateLogger("Api.System").LogWarning(ex, "system query failed for {System}", name);
                return Unavailable();
            }
        });

        api.MapGet("/carrier/{id}", async (IDockingReader reader, ILoggerFactory loggers, string id) =>
        {
            try
            {
                var days = await reader.GetCarrierDockingAsync(id);
                return Results.Ok(new CarrierResponse(
                    RedisKeys.NormalizeCarrier(id),
                    days.Select(d => new CarrierDay(d.Date.ToString(RedisKeys.DateFormat), d.DockingCount)).ToList()));
            }
            catch (Exception ex)
            {
                loggers.CreateLogger("Api.Carrier").LogWarning(ex, "carrier query failed for {Carrier}", id);
                return Unavailable();
            }
        });

        return app;
    }

    private static IResult Unavailable() => Results.Json(new { error = RedisUnavailable }, statusCode: 503);

    // Property names are the wire contract; the default web JSON options camel-case them to
    // system/visits/station/type/count/lastSeen/date/dockings, exactly as before.
    private sealed record VisitedSystem(string System, long Visits);

    private sealed record MostVisitedResponse(IReadOnlyList<VisitedSystem> Systems);

    /// <param name="LastSeen">Unix seconds.</param>
    private sealed record StationDocking(string Station, string Type, int Count, long LastSeen);

    private sealed record SystemResponse(string System, IReadOnlyList<StationDocking> Stations);

    /// <param name="Date">yyyy-MM-dd.</param>
    private sealed record CarrierDay(string Date, int Dockings);

    private sealed record CarrierResponse(string Carrier, IReadOnlyList<CarrierDay> Days);
}
