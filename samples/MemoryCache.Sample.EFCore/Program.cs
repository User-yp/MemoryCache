using MemoryCache;
using MemoryCache.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MemoryCache.Sample.EFCore;

internal static class Program
{
    private const string DefaultConnectionString =
        "Server=127.0.0.1;Port=3306;Database=quartz;User=root;Password=1234;SslMode=None";

    public static async Task<int> Main()
    {
        var connectionString =
            Environment.GetEnvironmentVariable("MEMORY_CACHE_MYSQL") ?? DefaultConnectionString;

        var services = new ServiceCollection();

        // EF Core 官方推荐的单例友好方式：DbContext 池。
        services.AddPooledDbContextFactory<JobConfigDbContext>(options =>
            options.UseMySql(connectionString, new MySqlServerVersion(new Version(9, 4, 0))));

        // 加载器注册到 DI，实体通过 [CacheEntity] + ScanAssembly 自动装配。
        services.AddSingleton<IEntityLoader<JobConfig>, JobConfigLoader>();
        services.AddEntityMemoryCache(builder =>
        {
            builder.ScanAssembly(typeof(JobConfig).Assembly);
            builder.WithWarmupOnStartup(false); // 示例手动触发首次加载
        });

        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<IEntityCacheService>();
        var loader = provider.GetRequiredService<IEntityLoader<JobConfig>>() as JobConfigLoader
            ?? throw new InvalidOperationException("无法解析 JobConfigLoader。");
        var jobConfigs = cache.Get<JobConfig>();

        await jobConfigs.ReloadAsync();
        Console.WriteLine(
            $"EF Core 全量加载 {jobConfigs.Count} 行，Version={jobConfigs.Version}，" +
            $"LoaderCalls={loader.LoadCount}。");

        foreach (var config in jobConfigs.GetSnapshot())
        {
            Console.WriteLine(
                $"  [{config.Group}/{config.JobKeyName}] cron={config.Cron}，" +
                $"trigger={config.TriggerKeyName}，enabled={config.IsEnabled}");
        }

        if (jobConfigs.Count > 0)
        {
            var first = jobConfigs.GetSnapshot()[0];
            var byKey = jobConfigs.GetByKey((first.Group, first.JobKeyName));
            Console.WriteLine(
                $"复合键查询 ({first.Group}, {first.JobKeyName}) => " +
                $"{byKey?.Cron ?? "<miss>"}");
        }

        _ = jobConfigs.GetSnapshot();
        _ = jobConfigs.AsQueryable().Count();
        Console.WriteLine(
            "后续查询全部走内存快照，LoaderCalls 仍为 "
            + $"{loader.LoadCount}。");

        return 0;
    }
}
