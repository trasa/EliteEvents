using EliteEvents.Eddn;
using EliteEvents.Eddn.Handlers;
using EliteEvents.Eddn.Journal;
using Newtonsoft.Json.Linq;

namespace EliteEvents.Visitors.Services;

public class EddnStreamReceiver : BackgroundService
{
    private readonly ILogger<EddnStreamReceiver> _logger;
    private readonly IEddnStream _eddnStream;
    private readonly IMessageFactory _messageFactory;
    private readonly IMessageHandlerProvider<JournalMessage, MessageEvent> _handlers;

    public EddnStreamReceiver(ILogger<EddnStreamReceiver> logger,
        IEddnStream eddnStream,
        IMessageFactory messageFactory,
        IMessageHandlerProvider<JournalMessage, MessageEvent> handlers)
    {
        _logger = logger;
        _eddnStream = eddnStream;
        _messageFactory = messageFactory;
        _handlers = handlers;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _eddnStream.Connect();
        while (!stoppingToken.IsCancellationRequested)
        {
            var str = _eddnStream.Receive();
            if (str != null)
            {
                var token = JToken.Parse(str);
                var message = _messageFactory.Create( token);
                if (message is JournalMessage journalMessage)
                {
                    _logger.LogInformation("Received journal message {Timestamp} - {Event}",
                        journalMessage.Message.Timestamp, journalMessage.Message.Event);
                    foreach (var handler in _handlers.GetMessageHandlers(journalMessage.Message.Event))
                    {
                        await handler.Handle(journalMessage);
                    }
                }
            }
        }
        _logger.LogInformation("Subscriber stopped");
    }

    public override void Dispose()
    {
        _logger.LogInformation("Subscriber disposed");
        _eddnStream.Dispose();
        base.Dispose();
    }
}
