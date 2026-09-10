using MemoryCache.Abstractions;
using MemoryCache.Redis;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MemoryCache.Tests;

/// <summary>
/// 两级缓存（L1 进程内 + L2 共享快照）的行为测试，使用假存储，不依赖真实 Redis。
/// </summary>
public sealed class RedisEntityCacheTests
{
    [Fact]
    public async Task Shared_snapshot_hit_avoids_the_database()
    {
        var inner = new SequenceLoader<SampleSwitch>(() => [Switch("db", "from-db")]);
        var store = new FakeRedisEntitySnapshotStore<SampleSwitch>();
        store.Seed([Switch("redis", "from-redis")]);

        await using var provider = CreateProvider(store, inner);
        var cache = provider.GetRequiredService<IEntityCacheService>().Get<SampleSwitch>();

        await cache.ReloadAsync();

        Assert.Equal("from-redis", cache.GetByKey("redis")?.Value);
        Assert.Equal(0, inner.LoadCount);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task Shared_snapshot_miss_loads_from_the_database_and_refills()
    {
        var inner = new SequenceLoader<SampleSwitch>(() => [Switch("db", "from-db")]);
        var store = new FakeRedisEntitySnapshotStore<SampleSwitch>();

        await using var provider = CreateProvider(store, inner);
        var cache = provider.GetRequiredService<IEntityCacheService>().Get<SampleSwitch>();

        await cache.ReloadAsync();

        Assert.Equal("from-db", cache.GetByKey("db")?.Value);
        Assert.Equal(1, inner.LoadCount);
        Assert.Single(store.Writes);
        Assert.Equal(1, store.LockAcquireCount);
        Assert.Equal(1, store.ReleaseCount);
    }

    [Fact]
    public async Task Shared_snapshot_unavailable_falls_back_to_the_database()
    {
        var inner = new SequenceLoader<SampleSwitch>(() => [Switch("db", "from-db")]);
        var store = new FakeRedisEntitySnapshotStore<SampleSwitch>
        {
            LockResult = RedisRefreshLockResult.Unavailable,
        };

        await using var provider = CreateProvider(store, inner);
        var cache = provider.GetRequiredService<IEntityCacheService>().Get<SampleSwitch>();

        await cache.ReloadAsync();

        Assert.Equal("from-db", cache.GetByKey("db")?.Value);
        Assert.Equal(1, inner.LoadCount);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task Busy_refresh_lock_waits_for_the_other_instance_to_refill()
    {
        var inner = new SequenceLoader<SampleSwitch>(() => [Switch("db", "from-db")]);
        var store = new FakeRedisEntitySnapshotStore<SampleSwitch>
        {
            LockResult = RedisRefreshLockResult.Busy,
        };

        var shared = new[] { Switch("redis", "from-redis") };
        store.ReadHandler = readCount => readCount >= 2
            ? new RedisEntitySnapshot<SampleSwitch>
            {
                Items = shared,
                LoadedAt = DateTimeOffset.UtcNow,
            }
            : null;

        await using var provider = CreateProvider(
            store,
            inner,
            options => options.RefreshLockWait = TimeSpan.FromMilliseconds(50));
        var cache = provider.GetRequiredService<IEntityCacheService>().Get<SampleSwitch>();

        await cache.ReloadAsync();

        Assert.Equal("from-redis", cache.GetByKey("redis")?.Value);
        Assert.Equal(0, inner.LoadCount);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task Entity_filter_keeps_the_entity_out_of_the_shared_snapshot()
    {
        var inner = new SequenceLoader<SampleSwitch>(() => [Switch("db", "from-db")]);
        var store = new FakeRedisEntitySnapshotStore<SampleSwitch>();

        await using var provider = CreateProvider(
            store,
            inner,
            options => options.EntityFilter = _ => false);
        var cache = provider.GetRequiredService<IEntityCacheService>().Get<SampleSwitch>();

        await cache.ReloadAsync();

        Assert.Equal("from-db", cache.GetByKey("db")?.Value);
        Assert.Equal(1, inner.LoadCount);
        Assert.Equal(0, store.ReadCount);
    }

    [Fact]
    public async Task Maintenance_refills_broadcasts_and_refreshes_local_cache()
    {
        var inner = new SequenceLoader<SampleSwitch>(() => [Switch("db", "from-db")]);
        var store = new FakeRedisEntitySnapshotStore<SampleSwitch>();
        var channel = new FakeInvalidationChannel();

        var services = CreateServices(store, inner);
        services.AddSingleton<IRedisEntityInvalidationChannel>(channel);

        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<IEntityCacheService>().Get<SampleSwitch>();
        var maintenance = provider.GetRequiredService<IRedisEntitySnapshotMaintenance>();

        await maintenance.RefreshFromSourceAsync<SampleSwitch>();

        Assert.Equal(1, inner.LoadCount);
        Assert.Single(store.Writes);
        Assert.Equal([typeof(SampleSwitch)], channel.Published);

        // 本进程已刷新，且是读共享快照得到的（没有第二次查库）。
        Assert.Equal(1, cache.Version);
        Assert.Equal("from-db", cache.GetByKey("db")?.Value);
    }

    [Fact]
    public void Invalid_options_are_rejected_at_registration_time()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            services.AddEntityMemoryCacheRedis(options => options.EntryTimeToLive = TimeSpan.Zero));
    }

    [Fact]
    public void Registering_redis_support_twice_throws()
    {
        var services = new ServiceCollection();
        services.AddEntityMemoryCacheRedis();

        Assert.Throws<InvalidOperationException>(() => services.AddEntityMemoryCacheRedis());
    }

    private static ServiceCollection CreateServices(
        FakeRedisEntitySnapshotStore<SampleSwitch> store,
        SequenceLoader<SampleSwitch> innerLoader,
        Action<RedisEntityCacheOptions>? configureRedis = null)
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
            configureRedis?.Invoke(options);
        });

        // 用假存储覆盖真实的 Redis 实现：装饰器解析到的就是它。
        services.AddSingleton<IRedisEntitySnapshotStore<SampleSwitch>>(store);

        return services;
    }

    private static ServiceProvider CreateProvider(
        FakeRedisEntitySnapshotStore<SampleSwitch> store,
        SequenceLoader<SampleSwitch> innerLoader,
        Action<RedisEntityCacheOptions>? configureRedis = null)
        => CreateServices(store, innerLoader, configureRedis).BuildServiceProvider();

    private static SampleSwitch Switch(string code, string value)
        => new()
        {
            Code = code,
            Value = value,
        };
}
