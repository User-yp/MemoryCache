using MemoryCache.Abstractions;
using MemoryCache.Redis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using Xunit;

namespace MemoryCache.Tests;

/// <summary>
/// 面向真实 Redis（127.0.0.1:6379）的集成测试。
/// Redis 不可用时用例直接跳过（不失败），便于在无 Redis 的环境中运行其余测试。
/// </summary>
[Trait("Category", "Integration")]
public sealed class RedisIntegrationTests
{
    [Fact]
    public async Task Shared_snapshot_is_reused_by_another_instance_and_carries_a_ttl()
    {
        if (!RedisTestSupport.IsAvailable)
        {
            return;
        }

        var keyPrefix = RedisTestSupport.NewKeyPrefix();
        var timeToLive = TimeSpan.FromSeconds(60);
        var loaderA = new SequenceLoader<SampleSwitch>(() => [Switch("A", "v1")]);
        var loaderB = new SequenceLoader<SampleSwitch>(() => [Switch("B", "should-not-be-used")]);

        try
        {
            await using var providerA = CreateServices(keyPrefix, loaderA, timeToLive)
                .BuildServiceProvider();
            await using var providerB = CreateServices(keyPrefix, loaderB, timeToLive)
                .BuildServiceProvider();

            var cacheA = providerA.GetRequiredService<IEntityCacheService>().Get<SampleSwitch>();
            var cacheB = providerB.GetRequiredService<IEntityCacheService>().Get<SampleSwitch>();

            // 实例 A：共享快照未命中 → 查库 → 回填 Redis。
            await cacheA.ReloadAsync();
            Assert.Equal(1, loaderA.LoadCount);
            Assert.Equal("v1", cacheA.GetByKey("A")?.Value);

            // 实例 B：直接命中共享快照，不查库。
            await cacheB.ReloadAsync();
            Assert.Equal(0, loaderB.LoadCount);
            Assert.Equal("v1", cacheB.GetByKey("A")?.Value);

            // 共享快照带 TTL。
            using var connection = await ConnectionMultiplexer.ConnectAsync(
                RedisTestSupport.Connection);
            var remaining = await connection
                .GetDatabase()
                .KeyTimeToLiveAsync(keyPrefix + typeof(SampleSwitch).FullName);

            Assert.NotNull(remaining);
            Assert.InRange(remaining!.Value, TimeSpan.FromSeconds(10), timeToLive);
        }
        finally
        {
            await DeleteKeysAsync(keyPrefix, typeof(SampleSwitch));
        }
    }

    [Fact]
    public async Task Expired_shared_snapshot_triggers_a_database_reload()
    {
        if (!RedisTestSupport.IsAvailable)
        {
            return;
        }

        var keyPrefix = RedisTestSupport.NewKeyPrefix();
        var loader = new SequenceLoader<SampleSwitch>(() => [Switch("A", "v1")]);

        try
        {
            await using var provider = CreateServices(
                    keyPrefix,
                    loader,
                    TimeSpan.FromSeconds(1))
                .BuildServiceProvider();
            var cache = provider.GetRequiredService<IEntityCacheService>().Get<SampleSwitch>();

            await cache.ReloadAsync();
            Assert.Equal(1, loader.LoadCount);

            // 等共享快照过期：下次刷新应回源数据库。
            await Task.Delay(TimeSpan.FromMilliseconds(1_500));
            await cache.ReloadAsync();

            Assert.Equal(2, loader.LoadCount);
        }
        finally
        {
            await DeleteKeysAsync(keyPrefix, typeof(SampleSwitch));
        }
    }

    [Fact]
    public async Task Invalidation_broadcast_makes_other_instances_reload_from_shared_snapshot()
    {
        if (!RedisTestSupport.IsAvailable)
        {
            return;
        }

        var keyPrefix = RedisTestSupport.NewKeyPrefix();
        var rows = new List<SampleSwitch> { Switch("A", "v1") };
        var loaderA = new SequenceLoader<SampleSwitch>(() => rows.ToArray());
        var loaderB = new SequenceLoader<SampleSwitch>(() => rows.ToArray());

        await using var providerA = CreateServices(keyPrefix, loaderA).BuildServiceProvider();
        await using var providerB = CreateServices(keyPrefix, loaderB).BuildServiceProvider();

        var hostedServicesA = providerA.GetServices<IHostedService>().ToArray();
        var hostedServicesB = providerB.GetServices<IHostedService>().ToArray();
        await StartAllAsync(hostedServicesA);
        await StartAllAsync(hostedServicesB);

        try
        {
            var cacheA = providerA.GetRequiredService<IEntityCacheService>().Get<SampleSwitch>();
            var cacheB = providerB.GetRequiredService<IEntityCacheService>().Get<SampleSwitch>();
            var maintenanceA = providerA.GetRequiredService<IRedisEntitySnapshotMaintenance>();
            var channelA = providerA.GetRequiredService<IRedisEntityInvalidationChannel>();

            await cacheA.ReloadAsync();
            await cacheB.ReloadAsync();
            Assert.Equal(1, loaderA.LoadCount);
            Assert.Equal(0, loaderB.LoadCount);
            Assert.Equal("v1", cacheB.GetByKey("A")?.Value);

            // 写入方改库后回填共享快照并广播。
            rows.Clear();
            rows.Add(Switch("A", "v2"));
            await maintenanceA.RefreshFromSourceAsync<SampleSwitch>();

            // 订阅建立可能滞后，允许重发广播；正常一次即可收敛。
            await WaitUntilAsync(async () =>
            {
                if (cacheB.GetByKey("A")?.Value == "v2")
                {
                    return true;
                }

                await channelA.PublishAsync(typeof(SampleSwitch));
                return false;
            });

            Assert.Equal("v2", cacheB.GetByKey("A")?.Value);

            // 实例 B 是从共享快照刷新的，没有回源查库。
            Assert.Equal(0, loaderB.LoadCount);

            // 实例 A：初始加载 1 次 + 回填时回源 1 次；回填后的本地刷新走共享快照，不重复查库。
            Assert.Equal(2, loaderA.LoadCount);
        }
        finally
        {
            await StopAllAsync(hostedServicesA);
            await StopAllAsync(hostedServicesB);
            await DeleteKeysAsync(keyPrefix, typeof(SampleSwitch));
        }
    }

    private static ServiceCollection CreateServices(
        string keyPrefix,
        SequenceLoader<SampleSwitch> innerLoader,
        TimeSpan? entryTimeToLive = null)
    {
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleSwitch>(entity => entity
                .WithKey(item => item.Code)
                .WithLoader(_ => innerLoader));
            builder.WithWarmupOnStartup(false);
        });

        services.AddEntityMemoryCacheRedis(options =>
        {
            options.Configuration = RedisTestSupport.Connection;
            options.KeyPrefix = keyPrefix;
            // 每个测试里的“实例”要有独立标识，否则会跳过对方的广播。
            options.InstanceId = Guid.NewGuid().ToString("N");
            options.TimeToLiveJitter = TimeSpan.Zero;
            if (entryTimeToLive is { } ttl)
            {
                options.EntryTimeToLive = ttl;
            }
        });

        return services;
    }

    private static async Task DeleteKeysAsync(string keyPrefix, params Type[] entityTypes)
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync(RedisTestSupport.Connection);
        var database = connection.GetDatabase();

        foreach (var entityType in entityTypes)
        {
            var key = keyPrefix + (entityType.FullName ?? entityType.Name);
            await database.KeyDeleteAsync(key);
            await database.KeyDeleteAsync(key + ":lock");
        }
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

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, int timeoutMilliseconds = 15_000)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (!await condition())
        {
            if (Environment.TickCount64 >= deadline)
            {
                throw new TimeoutException("Condition was not met within the timeout.");
            }

            await Task.Delay(200);
        }
    }

    private static SampleSwitch Switch(string code, string value)
        => new()
        {
            Code = code,
            Value = value,
        };
}
