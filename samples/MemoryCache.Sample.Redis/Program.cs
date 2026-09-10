using MemoryCache.Abstractions;
using MemoryCache.Redis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MemoryCache.Sample.Redis;

/// <summary>
/// 两级缓存端到端示例：进程内快照（L1）+ Redis 共享快照（L2）+ 跨进程失效广播。
/// 只需要本机 Redis（默认 <c>127.0.0.1:6379</c>），不依赖真实数据库。
/// </summary>
internal static class Program
{
    private const string DefaultRedis = "127.0.0.1:6379";

    public static async Task<int> Main()
    {
        var redisConnection =
            Environment.GetEnvironmentVariable("MEMORY_CACHE_REDIS") ?? DefaultRedis;

        // 每次运行使用独立键前缀；示例 TTL 很短，跑完自动过期，不会留下垃圾键。
        var keyPrefix = $"memorycache:sample:{Guid.NewGuid():N}:";

        // 假数据库：两个“实例”共享同一份数据，模拟它们连的是同一个库。
        var database = new FakeSwitchDatabase();
        database.Upsert(new FeatureSwitch { Code = "Feature:Report", Value = "on", Enabled = true });
        database.Upsert(new FeatureSwitch { Code = "Feature:Export", Value = "off", Enabled = false });

        var loaderA = new SwitchLoader(database);
        var loaderB = new SwitchLoader(database);

        // 两个容器 = 两个实例：各自持有进程内快照，共享同一个 Redis。
        await using var instanceA = BuildInstance("A", loaderA, redisConnection, keyPrefix);
        await using var instanceB = BuildInstance("B", loaderB, redisConnection, keyPrefix);

        // 示例用原生 ServiceProvider：托管服务要手工启动（这里就是 Redis 失效订阅器）。
        var hostedServicesA = instanceA.GetServices<IHostedService>().ToArray();
        var hostedServicesB = instanceB.GetServices<IHostedService>().ToArray();
        await StartAllAsync(hostedServicesA);
        await StartAllAsync(hostedServicesB);

        try
        {
            var cacheA = instanceA.GetRequiredService<IEntityCacheService>().Get<FeatureSwitch>();
            var cacheB = instanceB.GetRequiredService<IEntityCacheService>().Get<FeatureSwitch>();
            var maintenanceA = instanceA.GetRequiredService<IRedisEntitySnapshotMaintenance>();
            var channelA = instanceA.GetRequiredService<IRedisEntityInvalidationChannel>();

            Console.WriteLine($"Redis：{redisConnection}");
            Console.WriteLine($"键前缀：{keyPrefix}");
            Console.WriteLine();

            Console.WriteLine("=== 1. 实例 A 首次加载：Redis 未命中 → 查库 → 回填共享快照 ===");
            await cacheA.ReloadAsync();
            PrintState("A", cacheA, loaderA, database);

            Console.WriteLine("=== 2. 实例 B 首次加载：命中 Redis 共享快照，不查库 ===");
            await cacheB.ReloadAsync();
            PrintState("B", cacheB, loaderB, database);

            Console.WriteLine("=== 3. 写入方改库 → 回填 Redis → 广播其它实例 ===");
            database.Upsert(new FeatureSwitch
            {
                Code = "Feature:Report",
                Value = "off",
                Enabled = false,
            });
            await maintenanceA.RefreshFromSourceAsync<FeatureSwitch>();
            PrintState("A", cacheA, loaderA, database);

            Console.WriteLine("=== 4. 实例 B 收到广播：从共享快照刷新，不查库 ===");
            await WaitUntilAsync(async () =>
            {
                if (cacheB.GetByKey("Feature:Report")?.Value == "off")
                {
                    return true;
                }

                // 订阅建立可能滞后，允许重发；正常情况下一次就够。
                await channelA.PublishAsync(typeof(FeatureSwitch));
                return false;
            });
            PrintState("B", cacheB, loaderB, database);

            Console.WriteLine("=== 5. 等共享快照过期（示例 TTL 3 秒）后刷新：重新回源数据库 ===");
            await Task.Delay(TimeSpan.FromSeconds(4));
            await cacheA.ReloadAsync();
            PrintState("A", cacheA, loaderA, database);

            Console.WriteLine(
                "说明：示例把 L2 生存期设为 3 秒便于演示“过期回源”；生产建议 30 分钟 + 抖动" +
                "（见 README 第 14 节）。共享快照键到期后自动从 Redis 消失，无需手工清理。");
        }
        finally
        {
            await StopAllAsync(hostedServicesA);
            await StopAllAsync(hostedServicesB);
        }

        return 0;
    }

    private static ServiceProvider BuildInstance(
        string instanceName,
        SwitchLoader loader,
        string redisConnection,
        string keyPrefix)
    {
        var services = new ServiceCollection();

        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<FeatureSwitch>(entity => entity
                .WithKey(item => item.Code)
                .WithLoader(_ => loader));
            builder.WithWarmupOnStartup(false); // 示例手工触发首次加载
        });

        services.AddEntityMemoryCacheRedis(options =>
        {
            options.Configuration = redisConnection;
            options.KeyPrefix = keyPrefix;
            options.InstanceId = instanceName;                    // 实例标识：用于跳过自己发出的广播
            options.EntryTimeToLive = TimeSpan.FromSeconds(3);    // 示例取短值，便于演示过期回源
            options.TimeToLiveJitter = TimeSpan.FromMilliseconds(500);
        });

        return services.BuildServiceProvider();
    }

    private static void PrintState(
        string instanceName,
        IEntityCache<FeatureSwitch> cache,
        SwitchLoader loader,
        FakeSwitchDatabase database)
    {
        var rows = string.Join(
            ", ",
            cache.GetSnapshot()
                .OrderBy(row => row.Code)
                .Select(row => $"{row.Code}={row.Value}"));

        Console.WriteLine(
            $"   实例{instanceName}：版本={cache.Version} 条目={cache.Count} [{rows}]；" +
            $"该实例加载器调用 {loader.LoadCount} 次；数据库累计查询 {database.QueryCount} 次");
        Console.WriteLine();
    }

    private static async Task StartAllAsync(IEnumerable<IHostedService> hostedServices)
    {
        foreach (var service in hostedServices)
        {
            await service.StartAsync(CancellationToken.None);
        }
    }

    private static async Task StopAllAsync(IEnumerable<IHostedService> hostedServices)
    {
        foreach (var service in hostedServices)
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        int timeoutMilliseconds = 15_000)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (!await condition())
        {
            if (Environment.TickCount64 >= deadline)
            {
                throw new TimeoutException("等待实例收敛超时。");
            }

            await Task.Delay(200);
        }
    }
}
