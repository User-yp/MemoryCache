using MemoryCache;
using MemoryCache.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace MemoryCache.Sample;

internal static class Program
{
    private const string DefaultConnectionString =
        "Server=127.0.0.1;Port=3306;Database=quartz;User=root;Password=1234;SslMode=None";

    public static async Task<int> Main()
    {
        var connectionString =
            Environment.GetEnvironmentVariable("MEMORY_CACHE_MYSQL") ?? DefaultConnectionString;
        var loader = new JobConfigLoader(connectionString);

        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<JobConfig>(entity => entity
                .WithLoader(_ => loader)
                .WithRefreshInterval(TimeSpan.FromMinutes(1))
                .WithInvalidationMode(InvalidationMode.MarkStaleOnly));
        });

        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<IEntityCacheService>();
        var jobConfigs = cache.Get<JobConfig>();

        await jobConfigs.ReloadAsync();
        Console.WriteLine(
            $"Loaded {jobConfigs.Count} row(s), Version={jobConfigs.Version}, " +
            $"LoaderCalls={loader.LoadCount}, LoadedAt={jobConfigs.LoadedAt:O}");

        var snapshot = jobConfigs.GetSnapshot();
        foreach (var config in snapshot)
        {
            Console.WriteLine(
                $"  [{config.Group}/{config.JobKeyName}] cron={config.Cron}, " +
                $"trigger={config.TriggerKeyName}, enabled={config.IsEnabled}");
        }

        if (snapshot.Count > 0)
        {
            var first = snapshot[0];
            var byKey = jobConfigs.GetByKey((first.Group, first.JobKeyName));
            Console.WriteLine(
                $"Composite key lookup ({first.Group}, {first.JobKeyName}) => " +
                $"{byKey?.Cron ?? "<miss>"}");
        }

        _ = jobConfigs.GetSnapshot();
        _ = jobConfigs.AsQueryable().Count();
        Console.WriteLine(
            "Queries after reload are served from memory; " +
            $"LoaderCalls still {loader.LoadCount}.");

        return 0;
    }
}
