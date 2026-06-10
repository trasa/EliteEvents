using EliteEvents.Eddn.Journal;
using EliteEvents.Visitors.Services;

namespace EliteEvents.Visitors.Handlers;

public class JournalMessageHandler : IJournalMessageHandler
{
    private readonly ILogger<JournalMessageHandler> _logger;
    private readonly DockingRedisService _dockingService;
    private readonly IEventTickerService _eventTickerService;

    public MessageEvent[] Handles => [MessageEvent.Docked, MessageEvent.FSDJump];

    public JournalMessageHandler(ILogger<JournalMessageHandler> logger,
        DockingRedisService dockingService,
        IEventTickerService eventTickerService)
    {
        _logger = logger;
        _dockingService = dockingService;
        _eventTickerService = eventTickerService;
    }

    public async Task Handle(JournalMessage message)
    {
        switch (message.Message.Event)
        {
            case MessageEvent.Docked:
                await HandleDocked(message);
                break;
            case MessageEvent.FSDJump:
                await HandleFSDJump(message);
                break;
            case MessageEvent.Scan:
                break;
            case MessageEvent.Location:
                break;
            case MessageEvent.SAASignalsFound:
                break;
            case MessageEvent.CarrierJump:
                break;
            case MessageEvent.CodexEntry:
                break;
        }
    }

    private async Task HandleDocked(JournalMessage journal)
    {
        var ts = journal.Header.GatewayTimestamp;
        if (!journal.Message.AdditionalProperties.TryGetValue("StationType", out var stationType))
        {
            stationType = "Unknown";
        }

        if (!journal.Message.AdditionalProperties.TryGetValue("StationName", out var stationName))
        {
            stationName = "Unknown";
        }

        _logger.LogDebug("Handled Docked event at {System} -- {StationName} -- ({StationType})",
            journal.Message.StarSystem, stationName, stationType);

        if (stationType.ToString() == "FleetCarrier")
        {
            await _dockingService.RecordFleetCarrierDockingAsync(stationName?.ToString() ?? "Unknown", ts);
        }
        else
        {
            await _dockingService.RecordStationDockingAsync(journal.Message.StarSystem, stationName?.ToString() ?? "Unknown", stationType.ToString() ?? "Unknown", ts);
        }

        await _eventTickerService.PublishEvent(new
        {
            type = "docked",
            system = journal.Message.StarSystem,
            station = stationName?.ToString(),
            stationType = stationType.ToString(),
            ts = ts.ToUnixTimeSeconds(),
        });
    }

    private async Task HandleFSDJump(JournalMessage journal)
    {
        _logger.LogDebug("Handled FSDJump event to {System}", journal.Message.StarSystem);
        await _dockingService.RecordSystemVisitAsync(journal.Message.StarSystem);
        await _eventTickerService.PublishEvent(new
        {
            type = "fsdjump",
            system = journal.Message.StarSystem,
            ts = journal.Header.GatewayTimestamp.ToUnixTimeSeconds(),
        });
    }
}
