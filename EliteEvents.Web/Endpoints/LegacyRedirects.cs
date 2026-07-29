namespace EliteEvents.Web.Endpoints;

/// <summary>
/// The Blazor app's detail URLs, kept alive as permanent redirects to the canonical ones. Links
/// to elite-visitors.meancat.com have been shared for a couple of years; they should still land.
/// </summary>
public static class LegacyRedirects
{
    public static IEndpointRouteBuilder MapLegacyRedirects(this IEndpointRouteBuilder app)
    {
        app.MapGet("/system-details/{name}",
            (string name) => Results.Redirect($"/system/{Uri.EscapeDataString(name)}", permanent: true));

        app.MapGet("/carrier-details/{id}",
            (string id) => Results.Redirect($"/carrier/{Uri.EscapeDataString(id)}", permanent: true));

        return app;
    }
}
