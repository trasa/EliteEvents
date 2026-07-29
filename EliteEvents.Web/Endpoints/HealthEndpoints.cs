using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EliteEvents.Web.Endpoints;

/// <summary>
/// Health endpoints for four different audiences: the uptime monitor that watched the Blazor app
/// (<c>/health</c>), the one that watched the Next dashboard (<c>/api/health</c>, JSON), and the
/// k8s probes (<c>/health/live</c>, <c>/health/ready</c>).
/// </summary>
public static class HealthEndpoints
{
    private const string RedisCheckName = "redis";

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Everything, as plain text — what the old Blazor app served.
        app.MapHealthChecks("/health");

        // The Next dashboard's shape: {"status":"ok","redis":"ok"} / 503 {"status":"error",...}.
        app.MapHealthChecks("/api/health", new HealthCheckOptions
        {
            Predicate = check => check.Name == RedisCheckName,
            ResponseWriter = WriteDashboardJson
        });

        // Liveness runs no checks: a response at all means the process is serving.
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });

        // Readiness is Redis only — see the check registration in Program.cs for why the EDDN
        // stream is deliberately not part of it.
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready")
        });

        return app;
    }

    private static Task WriteDashboardJson(HttpContext http, HealthReport report)
    {
        var healthy = report.Status == HealthStatus.Healthy;
        http.Response.ContentType = "application/json";
        http.Response.Headers.CacheControl = "no-store";

        return http.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = healthy ? "ok" : "error",
            redis = healthy ? "ok" : "unavailable"
        }));
    }
}
