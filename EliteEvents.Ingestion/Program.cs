using EliteEvents.Eddn;
using EliteEvents.Eddn.Config;
using EliteEvents.Eddn.Handlers;
using EliteEvents.Eddn.Journal;
using EliteEvents.Eddn.Storage;
using EliteEvents.Ingestion;
using EliteEvents.Ingestion.Handlers;
using EliteEvents.Ingestion.Services;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

// The EDDN ingestion service: subscribes to the firehose and writes to Redis. It is a web host
// only so that k8s has HTTP probes to call — there is no UI, no static files, and the only
// endpoints are the health checks below.
//
// Consumers are shards, not replicas: EDDN broadcasts every message to every subscriber, so N
// unfiltered writers would count every event N times. Each instance handles only the slice of
// the feed matching its Eddn:ShardIndex; the FeedListener controller assigns those.
if (args.Contains(PurgeCommand.Flag))
{
    // Teardown mode, invoked by the FeedListener finalizer's drain Job. It shares this image
    // (and therefore RedisKeys) rather than living in the operator, so the keyspace has exactly
    // one definition. It connects, deletes, and exits — no stream, no host, no probes.
    return await PurgeCommand.RunAsync(args);
}

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
    .AddHostedService<EddnStreamReceiver>()
    .AddHostedService<SearchIndexMaintenanceService>();

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

// Structured feed health for the FeedListener controller. The probe endpoints answer a yes/no
// that Kubernetes can act on; this answers "is the subscription actually delivering, and is
// this shard getting its share of it" — the questions the CRD's status reports and that neither
// a Deployment nor a probe can express. It is read per-pod, so it deliberately describes this
// instance only.
app.MapGet("/health/stream", (StreamHealthTracker health, IMessageShardFilter shard) => Results.Ok(new
{
    lastMessageUtc = health.LastMessageUtc,
    messagesReceived = health.MessagesReceived,
    messagesHandled = health.MessagesHandled,
    shardIndex = shard.ShardIndex,
    shardCount = shard.ShardCount
}));

await app.RunAsync();
return 0;
