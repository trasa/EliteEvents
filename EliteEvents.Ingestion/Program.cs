using EliteEvents.Eddn.Config;
using EliteEvents.Eddn.Handlers;
using EliteEvents.Eddn.Journal;
using EliteEvents.Eddn.Storage;
using EliteEvents.Ingestion.Handlers;
using EliteEvents.Ingestion.Services;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

// The EDDN ingestion service: subscribes to the firehose and writes to Redis. It is a web host
// only so that k8s has HTTP probes to call — there is no UI, no static files, and the only
// endpoints are the two health checks below. It must run as a single writer (replicas: 1).
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

// Configuration / IOptions
builder.Services.Configure<EddnOptions>(builder.Configuration.GetSection("Eddn"));

// eddn
builder.Services.AddEddnStream()
    .AddSingleton<IMessageHandler<JournalMessage, MessageEvent>, JournalMessageHandler>();

// hosted services
builder.Services
    .AddSingleton<StreamHealthTracker>()
    .AddHostedService<EddnStreamReceiver>();

// redis — write side only. The reader is deliberately absent: this container never serves a
// query, and leaving IDockingReader unregistered makes that a compile-time fact.
builder.Services
    .AddEliteRedis(builder.Configuration)
    .AddEliteRedisWriter();

// health checks
builder.Services.AddHealthChecks()
    .AddCheck<RedisHealthCheck>("redis", tags: ["ready"])
    .AddCheck<EddnStreamHealthCheck>("eddn-stream", tags: ["ready"]);

var app = builder.Build();

// Liveness runs no checks at all: answering means the process is up and the request pipeline
// works. A quiet EDDN period or an unreachable Redis is a readiness problem, not a reason for
// k8s to restart a pod that is already retrying.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.Run();
