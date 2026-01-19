using EliteEvents.Eddn.Handlers;
using Microsoft.Extensions.DependencyInjection;

namespace EliteEvents.Eddn.Config;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddEddnStream(this IServiceCollection services)
    {
        services
            .AddSingleton<IHandlerProvider, HandlerProvider>()
            .AddSingleton<IEddnStream, EddnStream>()
            .AddSingleton<IMessageParser, MessageParser>();

        // schema handlers
        services
            .AddSingleton<JournalHandler>()
            .AddSingleton<ApproachSettlementHandler>()
            .AddSingleton<IEddnHandler>(sp => sp.GetRequiredService<JournalHandler>())
            .AddSingleton<IEddnHandler>(sp => sp.GetRequiredService<ApproachSettlementHandler>());


        return services;
    }
}
