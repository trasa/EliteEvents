using EliteEvents.Eddn.ApproachSettlement;
using EliteEvents.Eddn.Handlers;
using EliteEvents.Eddn.Journal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EliteEvents.Eddn.Config;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddEddnStream(this IServiceCollection services)
    {
        services
            .AddSingleton<IEddnStream, EddnStream>()
            // Built from options rather than registered by type so an out-of-range shard index
            // throws at startup, where a crash-looping pod makes the misconfiguration obvious,
            // rather than at the first message.
            .AddSingleton<IMessageShardFilter>(sp =>
            {
                var options = sp.GetRequiredService<IOptions<EddnOptions>>().Value;
                return new MessageShardFilter(options.ShardIndex, options.ShardCount);
            })
            .AddSingleton<IMessageFactory, MessageFactory>()
            .AddSingleton<IMessageHandlerProvider<JournalMessage, Journal.MessageEvent>, MessageHandlerProvider<JournalMessage, Journal.MessageEvent>>()
            .AddSingleton<IMessageHandlerProvider<ApproachSettlementMessage, ApproachSettlement.MessageEvent>, MessageHandlerProvider<ApproachSettlementMessage, ApproachSettlement.MessageEvent>>();

        return services;
    }
}
