using EliteEvents.Eddn;
using EliteEvents.Eddn.Config;
using EliteEvents.Eddn.Handlers;
using EliteEvents.Eddn.Journal;
using EliteEvents.Eddn.Storage;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace EliteEvents.Ingestion.Services;

public class EddnStreamReceiver : BackgroundService
{
    /// <summary>
    /// While processing keeps failing — Redis down, say — log once at the start of the outage
    /// and then only this often, rather than once per firehose message.
    /// </summary>
    private static readonly TimeSpan FailureLogInterval = TimeSpan.FromSeconds(30);

    private readonly ILogger<EddnStreamReceiver> _logger;
    private readonly IEddnStream _eddnStream;
    private readonly IMessageFactory _messageFactory;
    private readonly IMessageHandlerProvider<JournalMessage, MessageEvent> _handlers;
    private readonly StreamHealthTracker _streamHealth;
    private readonly IStreamHeartbeatWriter _heartbeat;
    private readonly EddnOptions _options;

    private int _consecutiveFailures;
    private DateTimeOffset _lastFailureLog = DateTimeOffset.MinValue;

    public EddnStreamReceiver(ILogger<EddnStreamReceiver> logger,
        IEddnStream eddnStream,
        IMessageFactory messageFactory,
        IMessageHandlerProvider<JournalMessage, MessageEvent> handlers,
        StreamHealthTracker streamHealth,
        IStreamHeartbeatWriter heartbeat,
        IOptions<EddnOptions> options)
    {
        _logger = logger;
        _eddnStream = eddnStream;
        _messageFactory = messageFactory;
        _handlers = handlers;
        _streamHealth = streamHealth;
        _heartbeat = heartbeat;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _eddnStream.Connect();
        // Seed the shared heartbeat the way StreamHealthTracker seeds itself, so the silence
        // threshold doubles as a startup grace window for the readiness probe too.
        await _heartbeat.RecordAsync(_streamHealth.LastMessageUtc, force: true);

        // Backs off between reconnect attempts so a persistently dead upstream isn't hammered.
        // Seeded to start time so a quiet startup doesn't trigger an immediate reconnect.
        var lastReconnect = DateTimeOffset.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            var str = _eddnStream.Receive();
            if (str != null)
            {
                _streamHealth.RecordMessage();
                await ProcessMessageAsync(str);
                await _heartbeat.RecordAsync(_streamHealth.LastMessageUtc);
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
    /// Parses and dispatches one frame. Every failure stays inside this method: an exception
    /// escaping <see cref="ExecuteAsync"/> stops the whole host under the default
    /// <c>BackgroundServiceExceptionBehavior.StopHost</c>, which turns any Redis blip into a
    /// crash loop. Dropping individual messages off a firehose is cheap by comparison, and the
    /// readiness probe is what reports the outage.
    /// </summary>
    private async Task ProcessMessageAsync(string json)
    {
        try
        {
            var token = JToken.Parse(json);
            var message = _messageFactory.Create(token);
            if (message is JournalMessage journalMessage)
            {
                foreach (var handler in _handlers.GetMessageHandlers(journalMessage.Message.Event))
                {
                    await handler.Handle(journalMessage);
                }
            }

            if (_consecutiveFailures > 0)
            {
                _logger.LogInformation("EDDN message processing recovered after {Failures} consecutive failures",
                    _consecutiveFailures);
                _consecutiveFailures = 0;
            }
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            var now = DateTimeOffset.UtcNow;
            if (_consecutiveFailures == 1 || now - _lastFailureLog > FailureLogInterval)
            {
                _lastFailureLog = now;
                _logger.LogError(ex, "Failed to process an EDDN message ({Failures} consecutive failures)",
                    _consecutiveFailures);
            }
        }
    }

    /// <summary>
    /// Reconnect when the stream has been silent past the threshold, but no more often than
    /// the threshold itself. The <see cref="StreamHealthTracker"/> clock is intentionally not
    /// reset here, so a reconnect that fails to restore traffic leaves the readiness check
    /// unhealthy as a manual-restart fallback.
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
