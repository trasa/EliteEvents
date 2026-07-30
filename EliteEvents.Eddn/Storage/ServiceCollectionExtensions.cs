using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace EliteEvents.Eddn.Storage;

public static class ServiceCollectionExtensions
{
    /// <summary>Connection string key. Same in every environment; the value comes from config or env.</summary>
    public const string ConnectionStringName = "Redis";

    /// <summary>
    /// Path to a file containing the Redis password, supplied as a Docker secret or a k8s Secret
    /// mounted as a file. Keeping the password out of the connection string means the same
    /// <c>ConnectionStrings__Redis</c> value is safe to put in plain compose/manifest YAML.
    /// </summary>
    public const string PasswordFileVariable = "REDIS_AUTH_FILE";

    private const string DefaultConnectionString = "localhost:6379";

    /// <summary>
    /// Registers the shared <see cref="IConnectionMultiplexer"/>. Call this once, then add the
    /// read and/or write side depending on what the host actually does.
    /// </summary>
    public static IServiceCollection AddEliteRedis(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton<IConnectionMultiplexer>(_ => Connect(configuration));

        // The EDDN heartbeat is registered here rather than on one side or the other: ingestion
        // writes it, the web tier reads it, and ingestion reads its own back for readiness.
        services.TryAddSingleton<RedisStreamHeartbeat>();
        services.TryAddSingleton<IStreamHeartbeatWriter>(sp => sp.GetRequiredService<RedisStreamHeartbeat>());
        services.TryAddSingleton<IStreamHeartbeatReader>(sp => sp.GetRequiredService<RedisStreamHeartbeat>());
        return services;
    }

    /// <summary>Read side: queries, the cached system count, live ticker subscription, health check.</summary>
    public static IServiceCollection AddEliteRedisReader(this IServiceCollection services)
    {
        services.TryAddSingleton<IDockingReader, DockingReader>();
        services.TryAddSingleton<ICachedSystemCount, CachedSystemCount>();
        services.TryAddSingleton<IEventSubscriber, RedisEventSubscriber>();
        return services;
    }

    /// <summary>Write side: docking records and live ticker publishing. Ingestion only.</summary>
    public static IServiceCollection AddEliteRedisWriter(this IServiceCollection services)
    {
        services.TryAddSingleton<WeeklyExpirationCalculator>();
        services.TryAddSingleton<IDockingWriter, DockingWriter>();
        services.TryAddSingleton<IEventPublisher, RedisEventPublisher>();

        // The search index is written here but read by the web tier, so it belongs on the write
        // side even though the reader depends on its output. Scheduling the rebuild is the host's
        // job — see SearchIndexMaintenanceService in Ingestion.
        services.TryAddSingleton<ISearchIndexMaintainer, SearchIndexMaintainer>();

        // Teardown is a write, and it deletes the same keys the maintainer builds. Registering
        // it here keeps the whole lifecycle of the index on one side of the reader/writer split.
        services.TryAddSingleton<ISearchIndexPurger, SearchIndexPurger>();
        return services;
    }

    private static IConnectionMultiplexer Connect(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName) ?? DefaultConnectionString;
        var options = ConfigurationOptions.Parse(connectionString);

        // Set the password on the parsed options rather than appending it to the connection
        // string, so a password containing a comma or '=' can't corrupt the parse.
        var passwordFile = Environment.GetEnvironmentVariable(PasswordFileVariable);
        if (!string.IsNullOrEmpty(passwordFile) && File.Exists(passwordFile))
        {
            options.Password = File.ReadAllText(passwordFile).Trim();
        }

        // Never abort on a failed initial connect: the app should start and keep retrying even if
        // Redis is briefly unavailable, which matters when both come up together in a cluster.
        options.AbortOnConnectFail = false;

        // Deliberately not logging the connection string or options.ToString() — either can carry
        // the password into stdout.
        Console.WriteLine(
            $"Connecting to Redis: endpoints=[{string.Join(", ", options.EndPoints)}] " +
            $"ssl={options.Ssl} password={(string.IsNullOrEmpty(options.Password) ? "no" : "yes")}");

        return ConnectionMultiplexer.Connect(options, Console.Out);
    }
}
