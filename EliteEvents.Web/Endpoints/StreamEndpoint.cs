using System.Text;
using EliteEvents.Web.Services;
using Microsoft.AspNetCore.Http.Features;

namespace EliteEvents.Web.Endpoints;

/// <summary>
/// The live ticker as Server-Sent Events. Each connection carries two views of the same feed:
/// <list type="bullet">
/// <item>the unnamed <c>message</c> event, whose data is the JSON the Next dashboard's ticker
/// consumed — unchanged, so existing consumers of <c>/api/stream</c> keep working;</item>
/// <item>a named <c>ticker</c> event carrying server-rendered HTML, which htmx swaps straight
/// into the page with no client-side templating.</item>
/// </list>
/// </summary>
public static class StreamEndpoint
{
    /// <summary>Comment frame sent while idle, so proxies and browsers don't drop a quiet stream.</summary>
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(25);

    public static IEndpointRouteBuilder MapStreamEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/stream", async (HttpContext http, LiveEventHub hub, CancellationToken cancellationToken) =>
        {
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache, no-transform";
            // Tells nginx-family proxies not to buffer; Caddy and the k8s ingress honour it too.
            http.Response.Headers["X-Accel-Buffering"] = "no";
            http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            using var client = hub.SubscribeClient();

            await WriteAsync(http, ": connected\n\n", cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                var ready = await WaitForFrameAsync(client, cancellationToken);
                if (!ready)
                {
                    // Either the keep-alive fired or the hub shut down; a comment frame is
                    // harmless in both cases and is what tells us the client has gone away.
                    await WriteAsync(http, ": ping\n\n", cancellationToken);
                    continue;
                }

                while (client.Reader.TryRead(out var frame))
                {
                    await WriteAsync(http, FormatFrame(frame), cancellationToken);
                }
            }
        });

        return app;
    }

    /// <summary>
    /// Waits for at least one frame, or gives up after <see cref="KeepAliveInterval"/> so the
    /// loop can emit a keep-alive.
    /// </summary>
    private static async Task<bool> WaitForFrameAsync(LiveEventHub.ClientSubscription client,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(KeepAliveInterval);
        try
        {
            return await client.Reader.WaitToReadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static string FormatFrame(TickerFrame frame)
    {
        var sb = new StringBuilder();

        // Default (unnamed) event: the JSON contract.
        sb.Append("data: ").Append(frame.Json).Append("\n\n");

        // Named event: rendered HTML. SSE is line-oriented, so multi-line markup needs one
        // data: line per line — the client rejoins them with newlines.
        sb.Append("event: ticker\n");
        foreach (var line in frame.Html.Split('\n'))
        {
            sb.Append("data: ").Append(line.TrimEnd('\r')).Append('\n');
        }
        sb.Append('\n');

        return sb.ToString();
    }

    private static async Task WriteAsync(HttpContext http, string payload, CancellationToken cancellationToken)
    {
        await http.Response.WriteAsync(payload, cancellationToken);
        await http.Response.Body.FlushAsync(cancellationToken);
    }
}
