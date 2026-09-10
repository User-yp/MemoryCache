using MemoryCache;
using MemoryCache.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MemoryCache.Tests;

public sealed class EntityCacheTests
{
    [Fact]
    public async Task Reload_populates_snapshot_and_key_index()
    {
        var loader = new SequenceLoader<SampleSwitch>(
            () => [Switch("A", "value-a"), Switch("B", "value-b", enabled: false)]);
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleSwitch>(entity => entity
                .WithKey(item => item.Code)
                .WithLoader(_ => loader));
        });

        await using var provider = services.BuildServiceProvider();
        var entry = provider.GetRequiredService<IEntityCacheService>().Get<SampleSwitch>();

        await entry.ReloadAsync();

        Assert.Equal(2, entry.Count);
        Assert.Equal(1, entry.Version);
        Assert.NotNull(entry.LoadedAt);
        Assert.Equal(1, loader.LoadCount);
        Assert.Equal("value-a", entry.GetByKey("A")?.Value);
        Assert.True(entry.ContainsKey("B"));
        Assert.True(entry.TryGetValue("B", out var item));
        Assert.False(item?.Enabled);
        Assert.False(entry.TryGetValue("missing", out _));
        Assert.Single(entry.AsQueryable().Where(item => item.Enabled));
    }

    [Fact]
    public async Task Reload_swaps_snapshot_atomically_and_increments_version()
    {
        var items = new List<SampleSwitch> { Switch("A", "v1") };
        var loader = new SequenceLoader<SampleSwitch>(() => items.ToArray());
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleSwitch>(entity => entity
                .WithKey(item => item.Code)
                .WithLoader(_ => loader));
        });

        await using var provider = services.BuildServiceProvider();
        var entry = provider.GetRequiredService<IEntityCacheService>().Get<SampleSwitch>();

        await entry.ReloadAsync();
        var oldSnapshot = entry.GetSnapshot();
        Assert.Single(oldSnapshot);

        items.Add(Switch("B", "v2"));
        await entry.ReloadAsync();

        Assert.Equal(2, entry.Count);
        Assert.Equal(2, entry.Version);
        Assert.Equal(2, loader.LoadCount);
        Assert.Single(oldSnapshot);
        Assert.Equal("v1", oldSnapshot[0].Value);
    }

    [Fact]
    public async Task Failed_reload_keeps_previous_snapshot_and_clears_error_on_retry()
    {
        var loader = new FailingAfterFirstLoader<SampleSwitch>(Switch("A", "v1"));
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleSwitch>(entity => entity
                .WithKey(item => item.Code)
                .WithLoader(_ => loader));
        });

        await using var provider = services.BuildServiceProvider();
        var entry = provider.GetRequiredService<IEntityCacheService>().Get<SampleSwitch>();

        await entry.ReloadAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => entry.ReloadAsync());
        Assert.Contains("Simulated loader failure", exception.Message);
        Assert.Equal(1, entry.Version);
        Assert.Equal(1, entry.Count);
        Assert.Equal(exception, entry.LastError);
        Assert.Equal("v1", entry.GetByKey("A")?.Value);

        await entry.ReloadAsync();

        Assert.Equal(2, entry.Version);
        Assert.Null(entry.LastError);
    }

    [Fact]
    public async Task Concurrent_reloads_are_single_flight()
    {
        var loader = new GateLoader<SampleSwitch>(Switch("A", "v1"));
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleSwitch>(entity => entity
                .WithKey(item => item.Code)
                .WithLoader(_ => loader));
        });

        await using var provider = services.BuildServiceProvider();
        var entry = provider.GetRequiredService<IEntityCacheService>().Get<SampleSwitch>();

        var reloads = Enumerable.Range(0, 20)
            .Select(_ => entry.ReloadAsync())
            .ToArray();

        await WaitUntilAsync(() => loader.LoadCount == 1);
        Assert.Equal(1, loader.LoadCount);

        loader.Release();
        await Task.WhenAll(reloads);

        Assert.Equal(1, entry.Version);
        Assert.Equal(1, loader.LoadCount);
    }

    [Fact]
    public async Task EnsureFresh_skips_when_fresh_and_invalidate_reloads_in_background()
    {
        var loader = new SequenceLoader<SampleSwitch>(
            () => [Switch("A", "v1")]);
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleSwitch>(entity => entity
                .WithKey(item => item.Code)
                .WithLoader(_ => loader));
        });

        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IEntityCacheService>();
        var entry = service.Get<SampleSwitch>();

        await entry.EnsureFreshAsync();
        await entry.EnsureFreshAsync();
        Assert.Equal(1, loader.LoadCount);

        service.Invalidate<SampleSwitch>();

        await WaitUntilAsync(() => entry.Version == 2);
        Assert.Equal(2, loader.LoadCount);

        await entry.EnsureFreshAsync();
        Assert.Equal(2, loader.LoadCount);
    }

    [Fact]
    public async Task MarkStaleOnly_invalidation_defers_reload_until_EnsureFresh()
    {
        var loader = new SequenceLoader<SampleSwitch>(() => [Switch("A", "v1")]);
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleSwitch>(entity => entity
                .WithKey(item => item.Code)
                .WithLoader(_ => loader)
                .WithInvalidationMode(InvalidationMode.MarkStaleOnly));
        });

        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IEntityCacheService>();
        var entry = service.Get<SampleSwitch>();

        await entry.EnsureFreshAsync();
        service.Invalidate<SampleSwitch>();

        Assert.True(entry.IsStale);
        Assert.Equal(1, entry.Version);
        Assert.Equal(1, loader.LoadCount);

        await entry.EnsureFreshAsync();

        Assert.False(entry.IsStale);
        Assert.Equal(2, entry.Version);
        Assert.Equal(2, loader.LoadCount);
    }

    [Fact]
    public async Task Composite_property_key_supports_tuple_lookup()
    {
        var loader = new SequenceLoader<SampleComposite>(
            () =>
            [
                new SampleComposite { Group = "batch", Code = "A", Value = "value-a" },
                new SampleComposite { Group = "batch", Code = "B", Value = "value-b" },
            ]);
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleComposite>(entity => entity
                .WithLoader(_ => loader));
        });

        await using var provider = services.BuildServiceProvider();
        var entry = provider.GetRequiredService<IEntityCacheService>().Get<SampleComposite>();

        await entry.ReloadAsync();

        Assert.Equal("value-a", entry.GetByKey(("batch", "A"))?.Value);
        Assert.True(entry.ContainsKey(("batch", "B")));
        Assert.False(entry.ContainsKey(("batch", "missing")));
        Assert.Null(entry.GetByKey(("other", "A")));
    }

    [Fact]
    public async Task Keyless_entity_supports_snapshot_queries_but_not_key_lookup()
    {
        var loader = new SequenceLoader<SampleKeylessEntity>(
            () => [new SampleKeylessEntity { Value = "v1" }]);
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleKeylessEntity>(entity => entity
                .WithLoader(_ => loader));
        });

        await using var provider = services.BuildServiceProvider();
        var entry = provider.GetRequiredService<IEntityCacheService>().Get<SampleKeylessEntity>();

        await entry.ReloadAsync();

        Assert.Equal(1, entry.Count);
        Assert.Equal("v1", entry.GetSnapshot()[0].Value);
        Assert.Throws<NotSupportedException>(() => entry.GetByKey("anything"));
        Assert.Throws<NotSupportedException>(() => entry.ContainsKey("anything"));
    }

    [Fact]
    public async Task Duplicate_key_policies_are_applied()
    {
        var duplicates = () => new[]
        {
            Switch("A", "first"),
            Switch("A", "last"),
        };

        Assert.Equal("last", await LoadWithDuplicatePolicyAsync(
            DuplicateKeyPolicy.LogWarningAndKeepLast, duplicates));
        Assert.Equal("first", await LoadWithDuplicatePolicyAsync(
            DuplicateKeyPolicy.KeepFirst, duplicates));

        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleSwitch>(entity => entity
                .WithKey(item => item.Code)
                .WithLoader(_ => new SequenceLoader<SampleSwitch>(duplicates))
                .WithDuplicateKeyPolicy(DuplicateKeyPolicy.Throw));
        });

        await using var provider = services.BuildServiceProvider();
        var entry = provider.GetRequiredService<IEntityCacheService>().Get<SampleSwitch>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => entry.ReloadAsync());
        Assert.Contains("Duplicate cache key", exception.Message);
        Assert.Equal(0, entry.Count);
    }

    [Fact]
    public async Task Scoped_di_loader_is_disposed_after_reload()
    {
        var before = ScopedTrackingLoader.DisposeCount;
        var services = new ServiceCollection();
        services.AddTransient<IEntityLoader<SampleSwitch>, ScopedTrackingLoader>();
        services.AddEntityMemoryCache(builder => builder.AddEntity<SampleSwitch>());

        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IEntityCacheService>();

        await service.ReloadAsync<SampleSwitch>();

        Assert.Equal(before + 1, ScopedTrackingLoader.DisposeCount);
        Assert.Equal(1, service.Get<SampleSwitch>().Version);
    }

    [Fact]
    public async Task ReloadAll_refreshes_every_registered_entity()
    {
        var switchLoader = new SequenceLoader<SampleSwitch>(() => [Switch("A", "v1")]);
        var compositeLoader = new SequenceLoader<SampleComposite>(
            () => [new SampleComposite { Group = "g", Code = "c" }]);
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleSwitch>(entity => entity
                .WithKey(item => item.Code)
                .WithLoader(_ => switchLoader));
            builder.AddEntity<SampleComposite>(entity => entity
                .WithLoader(_ => compositeLoader));
        });

        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IEntityCacheService>();

        await service.ReloadAllAsync();

        Assert.Equal(1, service.Get<SampleSwitch>().Version);
        Assert.Equal(1, service.Get<SampleComposite>().Version);
    }

    [Fact]
    public async Task ReloadAll_with_parallelism_of_one_runs_entities_strictly_sequentially()
    {
        var first = new GateLoader<SampleSwitch>(Switch("A", "v1"));
        var second = new GateLoader<SampleComposite>(
            new SampleComposite { Group = "g", Code = "c" });

        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleSwitch>(entity => entity
                .WithKey(item => item.Code)
                .WithLoader(_ => first));
            builder.AddEntity<SampleComposite>(entity => entity
                .WithLoader(_ => second));
            builder.WithWarmupOnStartup(false);
            builder.WithReloadAllParallelism(1);
        });

        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IEntityCacheService>();

        var reloadAll = service.ReloadAllAsync();
        await WaitUntilAsync(() => first.LoadCount + second.LoadCount == 1);

        // 并行度为 1 时，第二个实体必须等第一个实体加载完成才会开始。
        await Task.Delay(200);
        Assert.Equal(1, first.LoadCount + second.LoadCount);

        first.Release();
        second.Release();
        await reloadAll;

        Assert.Equal(1, service.Get<SampleSwitch>().Version);
        Assert.Equal(1, service.Get<SampleComposite>().Version);
    }

    [Fact]
    public async Task Reloaded_event_reports_versions_and_failures()
    {
        var loader = new FailingAfterFirstLoader<SampleSwitch>(Switch("A", "v1"));
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleSwitch>(entity => entity
                .WithKey(item => item.Code)
                .WithLoader(_ => loader));
        });

        await using var provider = services.BuildServiceProvider();
        var entry = provider.GetRequiredService<IEntityCacheService>().Get<SampleSwitch>();
        var events = new List<EntityCacheReloadedEventArgs<SampleSwitch>>();
        entry.Reloaded += (_, args) => events.Add(args);

        await entry.ReloadAsync();
        try
        {
            await entry.ReloadAsync();
        }
        catch (InvalidOperationException)
        {
            // 预期的失败；具体断言通过下方的事件参数完成
        }

        Assert.Equal(2, events.Count);
        Assert.Equal((0, 1), (events[0].PreviousVersion, events[0].NewVersion));
        Assert.False(events[0].Failed);
        Assert.Null(events[0].Error);
        Assert.Equal(1, events[1].PreviousVersion);
        Assert.Equal(1, events[1].NewVersion);
        Assert.True(events[1].Failed);
        Assert.NotNull(events[1].Error);
    }

    private static async Task<string> LoadWithDuplicatePolicyAsync(
        DuplicateKeyPolicy policy,
        Func<IReadOnlyCollection<SampleSwitch>> data)
    {
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleSwitch>(entity => entity
                .WithKey(item => item.Code)
                .WithLoader(_ => new SequenceLoader<SampleSwitch>(data))
                .WithDuplicateKeyPolicy(policy));
        });

        await using var provider = services.BuildServiceProvider();
        var entry = provider.GetRequiredService<IEntityCacheService>().Get<SampleSwitch>();

        await entry.ReloadAsync();

        return entry.GetByKey("A")?.Value ?? "";
    }

    private static SampleSwitch Switch(string code, string value, bool enabled = true)
        => new()
        {
            Code = code,
            Value = value,
            Enabled = enabled,
        };

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 5_000)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline)
            {
                throw new TimeoutException("Condition was not met within the timeout.");
            }

            await Task.Delay(10);
        }
    }
}
