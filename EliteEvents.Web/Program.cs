using EliteEvents.Eddn.Config;
using EliteEvents.Eddn.Storage;
using EliteEvents.Web.Components;
using EliteEvents.Web.Endpoints;
using EliteEvents.Web.Services;

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

var app = builder.Build();

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

app.Run();
