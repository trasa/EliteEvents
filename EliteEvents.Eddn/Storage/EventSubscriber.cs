using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using StackExchange.Redis;

namespace EliteEvents.Eddn.Storage;

/// <summary>
/// Receives live ticker frames from <see cref="RedisKeys.EventsChannel"/>. Read side.
/// <para>
/// This is deliberately a thin primitive: one call, one Redis subscription. The web tier layers
/// its own fan-out on top so a pod holds a single subscription no matter how many browsers are
/// connected to it.
/// </para>
/// </summary>
public interface IEventSubscriber
{
    /// <summary>
    /// Starts delivering events to <paramref name="onEvent"/>. Dispose the returned handle to
    /// unsubscribe. Malformed frames are logged and skipped rather than surfaced.
    /// </summary>
    Task<IAsyncDisposable> SubscribeAsync(Func<LiveEvent, Task> onEvent);
}

public class RedisEventSubscriber : IEventSubscriber
{
    private readonly ILogger<RedisEventSubscriber> _logger;
    private readonly IConnectionMultiplexer _redis;

    public RedisEventSubscriber(ILogger<RedisEventSubscriber> logger, IConnectionMultiplexer redis)
    {
        _logger = logger;
        _redis = redis;
    }

    public async Task<IAsyncDisposable> SubscribeAsync(Func<LiveEvent, Task> onEvent)
    {
        var queue = await _redis.GetSubscriber().SubscribeAsync(RedisKeys.EventsChannel);

        // OnMessage with an async callback processes frames sequentially, which is what we want:
        // ticker order should match publish order.
        queue.OnMessage(async message =>
        {
            LiveEvent? liveEvent;
            try
            {
                liveEvent = JsonConvert.DeserializeObject<LiveEvent>(message.Message.ToString());
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Discarding malformed live ticker frame");
                return;
            }

            if (liveEvent is null)
            {
                return;
            }

            try
            {
                await onEvent(liveEvent);
            }
            catch (Exception ex)
            {
                // A failing consumer must not tear down the subscription for everyone else.
                _logger.LogWarning(ex, "Live ticker subscriber threw while handling a {EventType} event",
                    liveEvent.Type);
            }
        });

        return new Subscription(queue);
    }

    private sealed class Subscription : IAsyncDisposable
    {
        private readonly ChannelMessageQueue _queue;

        public Subscription(ChannelMessageQueue queue) => _queue = queue;

        public async ValueTask DisposeAsync() => await _queue.UnsubscribeAsync();
    }
}
