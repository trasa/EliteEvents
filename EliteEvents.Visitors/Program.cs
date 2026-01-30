using EliteEvents.Eddn.Config;
using EliteEvents.Eddn.Handlers;
using EliteEvents.Eddn.Journal;
using EliteEvents.Visitors.Components;
using EliteEvents.Visitors.Handlers;
using EliteEvents.Visitors.Services;
using StackExchange.Redis;

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
    .AddSingleton<IJournalMessageHandler, JournalMessageHandler>()
    .AddSingleton<IMessageHandler<JournalMessage, MessageEvent>, JournalMessageHandler>();

// hosted services
builder.Services
    .AddHostedService<EddnStreamReceiver>();

// redis
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var environment = builder.Configuration.GetValue<string>("Environment")?.ToLower() ?? "local";
    var config = builder.Configuration.GetConnectionString($"redis-{environment}") ?? "localhost:6379";
    var passwordFile = Environment.GetEnvironmentVariable("REDIS_AUTH_FILE");
    if (File.Exists(passwordFile))
    {
        var password = File.ReadAllText(passwordFile).Trim();
        config += ",password=" + password;
    }
    Console.WriteLine($"Config: {config}");
    var options = ConfigurationOptions.Parse(config);
    options.AbortOnConnectFail = false;
    return ConnectionMultiplexer.Connect(options, Console.Out);
});
builder.Services
    .AddSingleton<DockingRedisService>()
    .AddSingleton<WeeklyExpirationCalculator>()
    .AddScoped<CachedSystemCount>();

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
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
//app.MapHub<EliteHub>("/elite-hub");
app.Run();
