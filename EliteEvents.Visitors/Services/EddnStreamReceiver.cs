using EliteEvents.Eddn;
using EliteEvents.Eddn.Config;
using EliteEvents.Eddn.Handlers;
using EliteEvents.Eddn.Journal;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace EliteEvents.Visitors.Services;

public class EddnStreamReceiver : BackgroundService
{
    private readonly ILogger<EddnStreamReceiver> _logger;
    private readonly IEddnStream _eddnStream;
    private readonly IMessageFactory _messageFactory;
    private readonly IMessageHandlerProvider<JournalMessage, MessageEvent> _handlers;
    private readonly StreamHealthTracker _streamHealth;
    private readonly EddnOptions _options;

    public EddnStreamReceiver(ILogger<EddnStreamReceiver> logger,
        IEddnStream eddnStream,
        IMessageFactory messageFactory,
        IMessageHandlerProvider<JournalMessage, MessageEvent> handlers,
        StreamHealthTracker streamHealth,
        IOptions<EddnOptions> options)
    {
        _logger = logger;
        _eddnStream = eddnStream;
        _messageFactory = messageFactory;
        _handlers = handlers;
        _streamHealth = streamHealth;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _eddnStream.Connect();
        // Backs off between reconnect attempts so a persistently dead upstream isn't hammered.
        // Seeded to start time so a quiet startup doesn't trigger an immediate reconnect.
        var lastReconnect = DateTimeOffset.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            var str = _eddnStream.Receive();
            if (str != null)
            {
                _streamHealth.RecordMessage();
                var token = JToken.Parse(str);
                var message = _messageFactory.Create( token);
                if (message is JournalMessage journalMessage)
                {
                    foreach (var handler in _handlers.GetMessageHandlers(journalMessage.Message.Event))
                    {
                        await handler.Handle(journalMessage);
                    }
                }
            }
            else if (ShouldReconnect(lastReconnect, out var silence))
            {
                _logger.LogWarning(
                    "No EDDN message for {Silence:F0}s; reconnecting the stream", silence.TotalSeconds);
                _eddnStream.Reconnect();
                lastReconnect = DateTimeOffset.UtcNow;
            }
        }
        _logger.LogInformation("Subscriber stopped");
    }

    /// <summary>
    /// Reconnect when the stream has been silent past the threshold, but no more often than
    /// the threshold itself. The <see cref="StreamHealthTracker"/> clock is intentionally not
    /// reset here, so a reconnect that fails to restore traffic still trips the Unhealthy
    /// health check (5 min) as a manual-restart fallback.
    /// </summary>
    private bool ShouldReconnect(DateTimeOffset lastReconnect, out TimeSpan silence)
    {
        var now = DateTimeOffset.UtcNow;
        silence = now - _streamHealth.LastMessageUtc;
        return silence > _options.ReconnectAfterSilence
            && now - lastReconnect > _options.ReconnectAfterSilence;
    }

    public override void Dispose()
    {
        _logger.LogInformation("Subscriber disposed");
        _eddnStream.Dispose();
        base.Dispose();
    }
}
