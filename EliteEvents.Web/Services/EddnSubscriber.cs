using System.Text;
using NetMQ;
using NetMQ.Sockets;
using Ionic.Zlib;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace EliteEvents.Web.Services;

public class EddnSubscriber : BackgroundService
{
    private readonly ILogger<EddnSubscriber> _logger;
    private readonly EddnOptions _options;

    public EddnSubscriber(ILogger<EddnSubscriber> logger, IOptions<EddnOptions> options)
    {
        _logger = logger;
        _options = options.Value;
        //_eventFirer = eventFirer;
        //_eventFirer.GetEvent<DockedEvent>()?.AddHandler((_, e) => PrintSimpleEvent(e));
        /*_eventFirer.MessageReceived += (s, e) =>
        {
            _logger.LogInformation("Eddn Event: {EventType}", e.EventType);
        };*/
    }


    protected override Task ExecuteAsync(CancellationToken stoppingToken)
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
                var result = utf8.GetString(uncompressed);
                //_logger.LogInformation("received: {Event}", result);
                var evt = JObject.Parse(result);
                var eventType = evt["message"]?.Value<string>("event");
                if (string.IsNullOrEmpty(eventType))
                {
                    _logger.LogInformation("unknown: {Message}", result);
                }
                else
                {
                    _logger.LogInformation("received event: {EventType}", eventType);
                }

            }
        }

        return Task.CompletedTask;
    }
}
