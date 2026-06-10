using Newtonsoft.Json;
using StackExchange.Redis;

namespace EliteEvents.Visitors.Services;

public interface IEventTickerService
{
    Task PublishEvent(object payload);
}

public class EventTickerService : IEventTickerService
{
    private readonly ILogger<EventTickerService> _logger;
    private readonly IConnectionMultiplexer _redis;

    public EventTickerService(ILogger<EventTickerService> logger, IConnectionMultiplexer redis)
    {
        _logger = logger;
        _redis = redis;
    }

    public async Task PublishEvent(object payload)
    {
        var sub = _redis.GetSubscriber();
        await sub.PublishAsync(
            RedisChannel.Literal("eddn:events"),
            JsonConvert.SerializeObject(payload));
    }
}
