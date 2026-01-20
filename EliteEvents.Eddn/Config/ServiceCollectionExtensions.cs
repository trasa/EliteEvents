using EliteEvents.Eddn.ApproachSettlement;
using EliteEvents.Eddn.Handlers;
using EliteEvents.Eddn.Journal;
using Microsoft.Extensions.DependencyInjection;

namespace EliteEvents.Eddn.Config;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddEddnStream(this IServiceCollection services)
    {
        services
            .AddSingleton<IEddnStream, EddnStream>()
            .AddSingleton<IMessageFactory, MessageFactory>()
            .AddSingleton<IMessageHandlerProvider<JournalMessage, Journal.MessageEvent>, MessageHandlerProvider<JournalMessage, Journal.MessageEvent>>()
            .AddSingleton<IMessageHandlerProvider<ApproachSettlementMessage, ApproachSettlement.MessageEvent>, MessageHandlerProvider<ApproachSettlementMessage, ApproachSettlement.MessageEvent>>();

        return services;
    }
}
