using System.Text;
using NetMQ;
using NetMQ.Sockets;
using Ionic.Zlib;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace EliteEvents.Web.Services.Eddn;

public class EddnSubscriber : BackgroundService
{
    private readonly ILogger<EddnSubscriber> _logger;
    private readonly HandlerProvider _handlerProvider;
    private readonly EddnOptions _options;

    public EddnSubscriber(ILogger<EddnSubscriber> logger,
        IOptions<EddnOptions> options,
        HandlerProvider handlerProvider)
    {
        _logger = logger;
        _handlerProvider = handlerProvider;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var utf8 = new UTF8Encoding();
        using var client = new SubscriberSocket();
        client.Options.ReceiveHighWatermark = _options.ReceiveHighWatermark;
        client.Connect(_options.StreamUrl);
        client.SubscribeToAnyTopic();
        while (!stoppingToken.IsCancellationRequested)
        {
            var b = client.ReceiveFrameBytes();
            var uncompressed = ZlibStream.UncompressBuffer(b);
            if (uncompressed != null)
            {
                var str = utf8.GetString(uncompressed);
                //_logger.LogInformation("Received: {MessageJson}", str);
                var token = JToken.Parse(str);
                var schema = token["$schemaRef"]?.Value<string>();
                var handler = _handlerProvider.GetHandler(schema);
                if (handler != null)
                {
                    await handler.Handle(token);
                }
            }
        }
        _logger.LogInformation("Subscriber stopped");
    }
}
