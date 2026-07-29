using EliteEvents.Eddn.Journal;
using EliteEvents.Eddn.Storage;

namespace EliteEvents.Ingestion.Handlers;

public class JournalMessageHandler : IJournalMessageHandler
{
    private readonly ILogger<JournalMessageHandler> _logger;
    private readonly IDockingWriter _dockingWriter;
    private readonly IEventPublisher _eventPublisher;

    public MessageEvent[] Handles => [MessageEvent.Docked, MessageEvent.FSDJump];

    public JournalMessageHandler(ILogger<JournalMessageHandler> logger,
        IDockingWriter dockingWriter,
        IEventPublisher eventPublisher)
    {
        _logger = logger;
        _dockingWriter = dockingWriter;
        _eventPublisher = eventPublisher;
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
            await _dockingWriter.RecordFleetCarrierDockingAsync(stationName?.ToString() ?? "Unknown", ts);
        }
        else
        {
            await _dockingWriter.RecordStationDockingAsync(journal.Message.StarSystem,
                stationName?.ToString() ?? "Unknown", stationType.ToString() ?? "Unknown", ts);
        }

        await _eventPublisher.PublishAsync(LiveEvent.Docked(
            journal.Message.StarSystem, stationName?.ToString(), stationType.ToString(), ts));
    }

    private async Task HandleFSDJump(JournalMessage journal)
    {
        _logger.LogDebug("Handled FSDJump event to {System}", journal.Message.StarSystem);
        await _dockingWriter.RecordSystemVisitAsync(journal.Message.StarSystem);
        await _eventPublisher.PublishAsync(
            LiveEvent.FsdJump(journal.Message.StarSystem, journal.Header.GatewayTimestamp));
    }
}
