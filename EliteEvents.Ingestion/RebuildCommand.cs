using EliteEvents.Eddn.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace EliteEvents.Ingestion;

/// <summary>
/// The scheduled half of index upkeep, run as a one-shot container by the CronJob the
/// FeedListener controller owns.
/// <para>
/// It is in this image for the same reason <see cref="PurgeCommand"/> is: the work is defined
/// entirely by <see cref="RedisKeys"/>, and the operator scanning those key patterns from Go
/// would put the one contract both containers share into a third language.
/// </para>
/// <para>
/// Running it as a Job rather than in every consumer is what makes it happen once. A rebuild is
/// a full pass over the system and carrier keyspaces; with consumers sharded, an in-process timer
/// runs that pass once per shard, doing N copies of identical work to produce one identical
/// result.
/// </para>
/// </summary>
public static class RebuildCommand
{
    public const string Flag = "--rebuild-indexes";

    public static async Task<int> RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Configuration
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables();

        builder.Services
            .AddEliteRedis(builder.Configuration)
            .AddEliteRedisWriter();

        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILogger<IHost>>();

        try
        {
            var maintainer = host.Services.GetRequiredService<ISearchIndexMaintainer>();

            var systems = await maintainer.RebuildSystemIndexAsync();
            var carriers = await maintainer.RebuildCarrierIndexAsync();

            logger.LogInformation(
                "Index rebuild complete; systems +{SystemsAdded}/-{SystemsRemoved} ({SystemsTotal} total), " +
                "carriers +{CarriersAdded}/-{CarriersRemoved} ({CarriersTotal} total)",
                systems.Added, systems.Removed, systems.Total,
                carriers.Added, carriers.Removed, carriers.Total);
            return 0;
        }
        catch (Exception ex)
        {
            // A non-zero exit is the only way the CronJob — and anyone reading its history —
            // learns the indexes are going stale. Search degrades silently otherwise: it keeps
            // answering, just from an index nothing is reconciling any more.
            logger.LogError(ex, "Index rebuild failed");
            return 1;
        }
    }
}
