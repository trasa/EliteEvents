using EliteEvents.Eddn;
using EliteEvents.Eddn.Handlers;
using EliteJournalReader;
using EliteEvents.Web.Components;
using EliteEvents.Web.Hubs;
using EliteEvents.Web.Services.Eddn;
using EliteEvents.Web.Services.Eddn.Handlers;
using EliteEvents.Web.Services.EliteJournal;
using Microsoft.Extensions.Options;

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
builder.Services.Configure<EliteJournalOptions>(builder.Configuration.GetSection("EliteJournal"));
builder.Services.Configure<EddnOptions>(builder.Configuration.GetSection("Eddn"));

// Elite Journal Reader Services
builder.Services
    .AddSingleton<JournalWatcher>(sp => new JournalWatcher(sp.GetRequiredService<IOptions<EliteJournalOptions>>().Value.Path))
    .AddSingleton<StatusWatcher>(sp => new StatusWatcher(sp.GetRequiredService<IOptions<EliteJournalOptions>>().Value.Path, false))
    .AddSingleton<JournalEventLoader>(_ => new JournalEventLoader(JournalEventLoader.FindHandlers()))
    .AddSingleton<JournalEventFirer>();

// message handlers
builder.Services.AddSingleton<HandlerProvider>()
    .AddSingleton<JournalHandler>()
    .AddSingleton<ApproachSettlementHandler>()
    .AddSingleton<IEddnHandler>(sp => sp.GetRequiredService<JournalHandler>())
    .AddSingleton<IEddnHandler>(sp => sp.GetRequiredService<ApproachSettlementHandler>());


// hosted services
builder.Services
    .AddHostedService<EliteJournalService>()
    .AddHostedService<EliteStatusService>()
    .AddHostedService<EddnSubscriber>();

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
app.MapHub<EliteHub>("/elite-hub");
app.Run();
