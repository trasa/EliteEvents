using EliteJournalReader;
using EliteJournalReader.Events;
using Meancat.EliteEvents.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Meancat.EliteEvents.Web.Services;

public class EliteJournalService : BackgroundService
{
    private readonly IHubContext<EliteHub> _hubContext;
    private readonly ILogger<EliteJournalService> _logger;
    private readonly JournalWatcher _journalWatcher;

    public EliteJournalService(IHubContext<EliteHub> hubContext,
        ILogger<EliteJournalService> logger,
        JournalWatcher journalWatcher)
    {
        _hubContext = hubContext;
        _logger = logger;
        _journalWatcher = journalWatcher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _journalWatcher.GetEvent<DockedEvent>().AddHandler(async void (_, e) => await BroadcastEvent(e.EventName, e, stoppingToken));
        await _journalWatcher.StartWatching(stoppingToken);
    }

    private async Task BroadcastEvent<T>(string eventName, T eventData, CancellationToken stoppingToken)
    {
        try
        {
            // TODO timestamp?
            await _hubContext.Clients.All.SendAsync("ReceiveEvent", eventName, eventData, cancellationToken: stoppingToken);
            _logger.LogDebug("Broadcasted event {EventName}", eventName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting event {EventName}", eventName);
        }
    }
}
