using EliteEvents.Eddn.Config;
using EliteEvents.Eddn.Storage;
using EliteEvents.Visitors.Components;

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
// EDDN ingestion lives in EliteEvents.Ingestion now; the options are still bound here because
// the stream health check reads its silence threshold from them.
builder.Services.Configure<EddnOptions>(builder.Configuration.GetSection("Eddn"));

// redis — read side only. This app serves queries and never writes docking data.
builder.Services
    .AddEliteRedis(builder.Configuration)
    .AddEliteRedisReader();

// health checks
builder.Services.AddHealthChecks()
    .AddCheck<RedisHealthCheck>("redis")
    .AddCheck<EddnStreamHealthCheck>("eddn-stream");

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services
    .AddHttpContextAccessor()
    .AddSignalR();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapHealthChecks("/health");
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
//app.MapHub<EliteHub>("/elite-hub");
app.Run();
