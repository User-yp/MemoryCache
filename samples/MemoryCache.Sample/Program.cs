using MemoryCache.Abstractions;
using MemoryCache.Samples.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace MemoryCache.Sample;

internal static class Program
{
    private const string DefaultConnectionString =
        "Server=127.0.0.1;Port=3306;Database=quartz;User=root;Password=1234;SslMode=None;" +
        "AllowPublicKeyRetrieval=True";

    public static async Task<int> Main()
    {
        var connectionString =
            Environment.GetEnvironmentVariable("MEMORY_CACHE_MYSQL") ?? DefaultConnectionString;

        // 表名可通过 MEMORY_CACHE_MYSQL_TABLE 自定义（默认 job_config）；
        // 库/表不存在时会自动创建并写入一行示例数据，方便一台干净机器直接跑起来。
        var tableName = SampleSchema.ResolveTableName();
        await SampleSchema.EnsureAsync(connectionString, tableName);
        Console.WriteLine($"数据表：{tableName}（缺失时自动建库建表 + 写入示例数据）");

        var loader = new JobConfigLoader(connectionString, tableName);

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
