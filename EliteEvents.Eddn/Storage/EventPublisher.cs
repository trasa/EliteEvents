using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using StackExchange.Redis;

namespace EliteEvents.Eddn.Storage;

/// <summary>
/// Publishes live ticker frames to <see cref="RedisKeys.EventsChannel"/>. Write side —
/// the ingestion service is the only publisher.
/// </summary>
public interface IEventPublisher
{
    Task PublishAsync(LiveEvent liveEvent);
}

public class RedisEventPublisher : IEventPublisher
{
    private readonly ILogger<RedisEventPublisher> _logger;
    private readonly ISubscriber _subscriber;

    public RedisEventPublisher(ILogger<RedisEventPublisher> logger, IConnectionMultiplexer redis)
    {
        _logger = logger;
        _subscriber = redis.GetSubscriber();
    }

    public async Task PublishAsync(LiveEvent liveEvent)
    {
        try
        {
            await _subscriber.PublishAsync(RedisKeys.EventsChannel, JsonConvert.SerializeObject(liveEvent));
        }
        catch (Exception ex)
        {
            // The ticker is decorative. Losing a frame must never cost us the docking record
            // that the same handler just wrote.
            _logger.LogWarning(ex, "Failed to publish {EventType} event to the live ticker", liveEvent.Type);
        }
    }
}
