using EliteEvents.Eddn;
using EliteEvents.Eddn.Handlers;

namespace EliteEvents.Web.Services.Eddn;

public class EddnStreamReceiver : BackgroundService
{
    private readonly ILogger<EddnStreamReceiver> _logger;
    private readonly IMessageParser _messageParser;
    private readonly IHandlerProvider _handlerProvider;
    private readonly IEddnStream _eddnStream;

    public EddnStreamReceiver(ILogger<EddnStreamReceiver> logger,
        IMessageParser messageParser,
        IHandlerProvider handlerProvider,
        IEddnStream eddnStream)
    {
        _logger = logger;
        _messageParser = messageParser;
        _handlerProvider = handlerProvider;
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
                var handler = _handlerProvider.FindHandler(token);
                if (handler != null)
                {
                    await handler.Handle(token);
                }
            }
        }
        _logger.LogInformation("Subscriber stopped");
    }
}
