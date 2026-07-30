using EliteEvents.Eddn.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace EliteEvents.Ingestion;

/// <summary>
/// The teardown half of a feed listener's lifecycle, run as a one-shot container by the
/// FeedListener finalizer's drain Job.
/// <para>
/// It is deliberately part of the ingestion image: the keys it deletes are defined in
/// <see cref="RedisKeys"/>, and the operator reimplementing those names in Go would create a
/// second copy of the one contract the containers share — the copy that drifts silently,
/// because a wrong key name here deletes nothing and reports success.
/// </para>
/// </summary>
public static class PurgeCommand
{
    public const string Flag = "--purge-indexes";

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
            var purger = host.Services.GetRequiredService<ISearchIndexPurger>();
            var removed = await purger.PurgeAsync();
            logger.LogInformation("Feed listener drain complete; {Removed} keys removed", removed);
            return 0;
        }
        catch (Exception ex)
        {
            // A non-zero exit is what tells the Job — and through it the finalizer — that the
            // drain did not happen. Swallowing this would release the finalizer on a lie.
            logger.LogError(ex, "Feed listener drain failed");
            return 1;
        }
    }
}
