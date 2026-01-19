using EliteEvents.Eddn.Journal;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace EliteEvents.Eddn.Handlers;

public class JournalHandler : IEddnHandler
{
    private readonly ILogger<JournalHandler> _logger;
    public string Schema => "https://eddn.edcd.io/schemas/journal/1";

    public JournalHandler(ILogger<JournalHandler> logger)
    {
        _logger = logger;
    }

    public async Task Handle(JToken token)
    {
        var message = token.ToObject<JournalMessage>();
        if (message == null)
        {
            _logger.LogWarning("Unable to deserialize journal message {@Token}", token);
            return;
        }
        await Handle(message);
    }

    private async Task Handle(JournalMessage journalMessage)
    {
        switch (journalMessage.Message.Event)
        {
            case MessageEvent.Docked:
                await HandleDocked(journalMessage.Message);
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
