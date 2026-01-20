using EliteEvents.Eddn;
using EliteEvents.Eddn.SchemaHandlers;

namespace EliteEvents.Visitors.Services;

public class EddnStreamReceiver : BackgroundService
{
    private readonly ILogger<EddnStreamReceiver> _logger;
    private readonly IMessageParser _messageParser;
    private readonly ISchemaHandlerProvider _schemaHandlerProvider;
    private readonly IEddnStream _eddnStream;

    public EddnStreamReceiver(ILogger<EddnStreamReceiver> logger,
        IMessageParser messageParser,
        ISchemaHandlerProvider schemaHandlerProvider,
        IEddnStream eddnStream)
    {
        _logger = logger;
        _messageParser = messageParser;
        _schemaHandlerProvider = schemaHandlerProvider;
        _eddnStream = eddnStream;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _eddnStream.Connect();
        while (!stoppingToken.IsCancellationRequested)
        {
            var str = _eddnStream.Receive();
            if (str != null)
            {
                //_logger.LogInformation("Received: {MessageJson}", str);
                var token = _messageParser.Parse(str);
                var message = _messageFactory.Create(token);
                var handler = _schemaHandlerProvider.FindHandler(token);
                if (handler != null)
                {
                    await handler.Handle(token);
                }
            }
        }
        _logger.LogInformation("Subscriber stopped");
    }
}
