using EliteEvents.Eddn.MessageHandlers;
using EliteEvents.Eddn.SchemaHandlers;
using Microsoft.Extensions.DependencyInjection;

namespace EliteEvents.Eddn.Config;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddEddnStream(this IServiceCollection services)
    {
        services
            .AddSingleton<ISchemaHandlerProvider, SchemaHandlerProvider>()
            .AddSingleton<IEddnStream, EddnStream>()
            .AddSingleton<IMessageParser, MessageParser>()
            .AddSingleton<IMessageFactory, MessageFactory>();

        // schema handlers
        services
            .AddSingleton<JournalHandler>()
            .AddSingleton<ApproachSettlementHandler>()
            .AddSingleton<IEddnHandler>(sp => sp.GetRequiredService<JournalHandler>())
            .AddSingleton<IEddnHandler>(sp => sp.GetRequiredService<ApproachSettlementHandler>());

        // message handlers
        services.AddSingleton<IMessageHandlerProvider, MessageHandlerProvider>();

        return services;
    }
}
