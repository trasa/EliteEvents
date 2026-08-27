using EliteEvents.Eddn.Config;
using EliteEvents.Eddn.Storage;
using EliteEvents.Web.Components;
using EliteEvents.Web.Endpoints;
using EliteEvents.Web.Services;
using OpenTelemetry.Metrics;

// The public web tier: reads Redis, renders HTML, and never writes. It is deliberately
// stateless — static SSR Razor Components (no circuit, no WebSocket) plus htmx — so any pod can
// serve any request and no session affinity is needed at the ingress.
var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);
if (builder.Environment.IsDevelopment())
{
    var localUser = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.LocalUser.json");
    builder.Configuration.AddJsonFile(localUser, optional: true, reloadOnChange: true);
}
builder.Configuration.AddEnvironmentVariables();

// Bound for the stream health check, which reports how long ago EDDN last said anything.
builder.Services.Configure<EddnOptions>(builder.Configuration.GetSection("Eddn"));

// How long Redis may be unreachable before this pod is considered unrecoverable. See
// RedisConnectivityState for the outage this exists to end.
builder.Services.Configure<RedisLivenessOptions>(builder.Configuration.GetSection("RedisLiveness"));

// redis — read side only. Ingestion owns every write.
builder.Services
    .AddEliteRedis(builder.Configuration)
    .AddEliteRedisReader()
    .AddRedisLivenessWatchdog();

// Live ticker: one Redis subscription per pod, fanned out to that pod's SSE clients.
builder.Services
    .AddSingleton<TickerFragmentRenderer>()
    .AddSingleton<LiveEventHub>()
    .AddHostedService(sp => sp.GetRequiredService<LiveEventHub>());

// Static SSR only: no AddInteractiveServerComponents(), no @rendermode, no blazor.web.js.
builder.Services.AddRazorComponents();

builder.Services.AddHealthChecks()
    // Only Redis gates readiness. The EDDN stream check is reported on /health for the uptime
    // monitor but deliberately left out of the "ready" set: a quiet firehose is no reason to pull
    // every web pod out of the service when they are still serving 30-day data perfectly well.
    .AddCheck<RedisHealthCheck>("redis", tags: ["ready"])
    // Liveness is *not* the readiness check with a longer fuse: it fails only when Redis has been
    // continuously unreachable for RedisLiveness:UnreachableRestartThreshold, which a pod that is
    // merely retrying never reaches. "Redis reachable but empty" is unready, never unalive —
    // restarting a pod cannot conjure data, and this check would loop it forever if it counted.
    .AddCheck<RedisLivenessHealthCheck>("redis-liveness", tags: ["live"])
    .AddCheck<EddnStreamHealthCheck>("eddn-stream");

// Prometheus metrics. Nothing here instruments the app by hand: ASP.NET Core already publishes a
// Microsoft.AspNetCore.Hosting meter, whose http.server.request.duration histogram carries a
// per-route request count. That count is the visit graph — the exporter renders it as
// http_server_request_duration_seconds_count, labelled with the route *template* rather than the
// filled URL, so /system/{SystemName} is one series no matter how many systems get looked at.
//
// System.Runtime is along for the ride and is the line to delete if this ever needs trimming. It
// costs one meter and gives GC heap size and working set per pod, which is the measurement the
// DOTNET_GCHeapHardLimitPercent note in k8s/30-web.yaml asks for and has so far been taken by
// hand after every change.
// Which port the scrape endpoint answers on. Configurable rather than a const purely so local
// development can move off 9090: that is the conventional port for `kubectl port-forward
// svc/prometheus`, and a dev run holding it means the port-forward either fails to bind or you
// spend a while reading the local app's metrics believing they are the cluster's. In the
// container it stays 9090, where the pod's network namespace has nothing to collide with.
var metricsPort = builder.Configuration.GetValue("Metrics:Port", 9090);

builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter("Microsoft.AspNetCore.Hosting")
        .AddMeter("System.Runtime")
        .AddPrometheusExporter());

var app = builder.Build();

// /metrics answers on the metrics port and nowhere else, and the metrics port answers /metrics and
// nothing else. Both halves matter, because the ingress routes `path: /` Prefix to this app: with
// the scrape endpoint on the main port, elite.meancat.com/metrics would be public.
//
// The test is the local port the connection actually arrived on, not the Host header. RequireHost
// ("*:9090") reads like the same check and is not one — ingress-nginx forwards the client's Host
// verbatim, so anyone willing to send `Host: x:9090` would come straight through it.
//
// Registered before everything else so a rejection is a bare 404: UseStatusCodePagesWithReExecute
// is added further down, which puts it *inside* this middleware, and short-circuiting here means
// it never runs. A scrape of the wrong port should not cost a re-executed Razor render.
app.Use(async (context, next) =>
{
    var onMetricsPort = context.Connection.LocalPort == metricsPort;
    var wantsMetrics = context.Request.Path.StartsWithSegments("/metrics");
    if (wantsMetrics != onMetricsPort)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await next(context);
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// No UseHttpsRedirection: TLS is terminated at the proxy (Caddy today, the k8s ingress next),
// and the container only ever listens on plain HTTP.
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseStaticFiles();

// Every page and fragment endpoint in this app is a GET, so no antiforgery token is ever issued
// or validated and no shared Data Protection key ring is required across pods. The middleware is
// still here because MapRazorComponents expects it. If a POST is ever added, the key ring has to
// move to Redis (AddDataProtection().PersistKeysToStackExchangeRedis(...)) or a pod restart will
// start rejecting tokens issued by another pod.
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>();

app.MapHealthEndpoints();
app.MapApiEndpoints();
app.MapFragmentEndpoints();
app.MapStreamEndpoint();
app.MapLegacyRedirects();

// Metrics:Port only decides which port this endpoint *answers* on; it does not open the socket.
// That is ASPNETCORE_HTTP_PORTS in Dockerfile.web and applicationUrl in launchSettings.json, and
// k8s/30-web.yaml's prometheus.io/port is what Prometheus dials. None of the four can read the
// others, and a disagreement fails quietly: the scrape 404s and the site keeps serving.
app.MapPrometheusScrapingEndpoint();

app.Run();
