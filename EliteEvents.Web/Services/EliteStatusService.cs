using EliteJournalReader;
using EliteJournalReader.Events;
using EliteEvents.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace EliteEvents.Web.Services;

public class EliteStatusService : BackgroundService
{
    private readonly IHubContext<EliteHub> _hubContext;
    private readonly ILogger<EliteStatusService> _logger;
    private readonly StatusWatcher _statusWatcher;

    public EliteStatusService(IHubContext<EliteHub> hubContext,
        ILogger<EliteStatusService> logger,
        StatusWatcher statusWatcher)
    {
        _hubContext = hubContext;
        _logger = logger;
        _statusWatcher = statusWatcher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _statusWatcher.StatusUpdated += async (_, e) =>
        {
            await BroadcastEvent(e, stoppingToken);
        };
        await _statusWatcher.StartWatching(stoppingToken);
    }

    private async Task BroadcastEvent(StatusFileEvent statusEvent, CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Status: {@statusEvent}", statusEvent);
            await _hubContext.Clients.All.SendAsync("ReceiveStatus", statusEvent, cancellationToken: stoppingToken);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error broadcasting status event");
        }
    }
}
