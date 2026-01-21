using EliteEvents.Eddn.Journal;
using EliteEvents.Visitors.Services;

namespace EliteEvents.Visitors.Handlers;

public class JournalMessageHandler : IJournalMessageHandler
{
    private readonly ILogger<JournalMessageHandler> _logger;
    private readonly DockingRedisService _dockingService;

    public MessageEvent[] Handles => [MessageEvent.Docked];

    public JournalMessageHandler(ILogger<JournalMessageHandler> logger, DockingRedisService dockingService)
    {
        _logger = logger;
        _dockingService = dockingService;
    }

    public async Task Handle(JournalMessage message)
    {
        switch (message.Message.Event)
        {
            case MessageEvent.Docked:
                await HandleDocked(message);
                break;
            case MessageEvent.FSDJump:
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

        _logger.LogInformation("Handled Docked event at {System} -- {StationName} -- ({StationType})",
            journal.Message.StarSystem, stationName, stationType);

        if (stationType.ToString() == "FleetCarrier")
        {
            await _dockingService.RecordFleetCarrierDockingAsync(stationName?.ToString() ?? "Unknown", ts);
        }
        else
        {
            await _dockingService.RecordStationDockingAsync(journal.Message.StarSystem, stationName?.ToString() ?? "Unknown", stationType?.ToString() ?? "Unknown", ts);
        }
    }
}
