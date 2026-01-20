using EliteEvents.Eddn.Journal;

namespace EliteEvents.Visitors.Handlers;

public class JournalMessageHandler : IJournalMessageHandler
{
    private readonly ILogger<JournalMessageHandler> _logger;

    public MessageEvent[] Handles => [MessageEvent.Docked];

    public JournalMessageHandler(ILogger<JournalMessageHandler> logger)
    {
        _logger = logger;
    }

    public async Task Handle(JournalMessage message)
    {
        switch (message.Message.Event)
        {
            case MessageEvent.Docked:
                await HandleDocked(message.Message);
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

    private Task HandleDocked(Message message)
    {
        message.AdditionalProperties.TryGetValue("StationType", out var stationType);
        message.AdditionalProperties.TryGetValue("StationName", out var stationName);
        _logger.LogInformation("Handled Docked event at {System} System -- {StationName} station ({StationType})",
            message.StarSystem, stationName, stationType);

        return Task.CompletedTask;
    }
}
