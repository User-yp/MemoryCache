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
    public async Task Cancelling_a_reload_aborts_the_load_without_touching_the_cache()
    {
        var loader = new GateLoader<SampleSwitch>(Switch("A", "v1"));
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleSwitch>(entity => entity
                .WithKey(item => item.Code)
                .WithLoader(_ => loader));
            builder.WithWarmupOnStartup(false);
        });

        await using var provider = services.BuildServiceProvider();
        var entry = provider.GetRequiredService<IEntityCacheService>().Get<SampleSwitch>();
        var reloadedEvents = 0;
        entry.Reloaded += (_, _) => reloadedEvents++;

        using var cancellation = new CancellationTokenSource();
        var reload = entry.ReloadAsync(cancellation.Token);
        await WaitUntilAsync(() => loader.LoadCount == 1);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reload);

        // 取消令牌被真正传给了加载器。
        await WaitUntilAsync(() => loader.ObservedCancellation);

        // 取消不是加载失败：旧快照保留，不记错误、不触发事件。
        Assert.Equal(0, entry.Version);
        Assert.Equal(0, entry.Count);
        Assert.Null(entry.LastError);
        Assert.False(entry.IsStale);
        Assert.Equal(0, reloadedEvents);

        // 取消之后缓存仍可正常刷新。
        var retry = entry.ReloadAsync();
        loader.Release();
        await retry;

        Assert.Equal(1, entry.Version);
        Assert.Equal(1, entry.Count);
        Assert.Equal("v1", entry.GetByKey("A")?.Value);
    }

    [Fact]
    public async Task Load_timeout_aborts_the_load_and_is_recorded_as_a_failure()
    {
        var loader = new GateLoader<SampleSwitch>(Switch("A", "v1"));
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleSwitch>(entity => entity
                .WithKey(item => item.Code)
                .WithLoader(_ => loader)
                .WithLoadTimeout(TimeSpan.FromMilliseconds(150)));
            builder.WithWarmupOnStartup(false);
        });

        await using var provider = services.BuildServiceProvider();
        var entry = provider.GetRequiredService<IEntityCacheService>().Get<SampleSwitch>();
        var events = new List<EntityCacheReloadedEventArgs<SampleSwitch>>();
        entry.Reloaded += (_, args) => events.Add(args);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => entry.ReloadAsync());

        Assert.Contains("load timeout", exception.Message);
        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);

        // 超时真正终止了取数。
        await WaitUntilAsync(() => loader.ObservedCancellation);

        // 与调用方取消不同：超时按刷新失败记录，且不产生半成品快照。
        Assert.Equal(0, entry.Version);
        Assert.Equal(0, entry.Count);
        Assert.Same(exception, entry.LastError);
        Assert.Single(events);
        Assert.True(events[0].Failed);
        Assert.Same(exception, events[0].Error);
    }

    [Fact]
    public async Task EnsureFresh_retries_after_a_load_timeout()
    {
        var loader = new GateLoader<SampleSwitch>(Switch("A", "v1"));
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleSwitch>(entity => entity
                .WithKey(item => item.Code)
                .WithLoader(_ => loader)
                .WithLoadTimeout(TimeSpan.FromMilliseconds(150)));
            builder.WithWarmupOnStartup(false);
        });

        await using var provider = services.BuildServiceProvider();
        var entry = provider.GetRequiredService<IEntityCacheService>().Get<SampleSwitch>();

        await Assert.ThrowsAsync<TimeoutException>(() => entry.EnsureFreshAsync());
        Assert.Equal(1, loader.LoadCount);

        // 上次刷新失败会让 EnsureFreshAsync 再试一次；这次放行加载器即可成功。
        var retry = entry.EnsureFreshAsync();
        loader.Release();
        await retry;

        Assert.Equal(2, loader.LoadCount);
        Assert.Equal(1, entry.Version);
        Assert.Equal("v1", entry.GetByKey("A")?.Value);
        Assert.Null(entry.LastError);
    }

    [Fact]
    public async Task Load_timeout_declared_on_the_attribute_is_applied()
    {
        var loader = new GateLoader<SampleTimeoutEntity>(new SampleTimeoutEntity { Value = "v1" });
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleTimeoutEntity>(entity => entity
                .WithLoader(_ => loader));
            builder.WithWarmupOnStartup(false);
        });

        await using var provider = services.BuildServiceProvider();
        var entry = provider.GetRequiredService<IEntityCacheService>().Get<SampleTimeoutEntity>();

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => entry.ReloadAsync());

        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
        await WaitUntilAsync(() => loader.ObservedCancellation);
        Assert.Equal(0, entry.Version);
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

    [Fact]
    public async Task Null_key_entries_stay_in_the_snapshot_but_are_not_indexed_by_default()
    {
        var loader = new SequenceLoader<SampleSwitch>(
            () => [Switch(null!, "broken"), Switch("A", "v1")]);
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleSwitch>(entity => entity
                .WithKey(item => item.Code)
                .WithLoader(_ => loader));
            builder.WithWarmupOnStartup(false);
        });

        await using var provider = services.BuildServiceProvider();
        var entry = provider.GetRequiredService<IEntityCacheService>().Get<SampleSwitch>();

        await entry.ReloadAsync();

        // 一条脏数据不再让整表刷新失败：正常条目照常可查。
        Assert.Equal(1, entry.Version);
        Assert.Null(entry.LastError);
        Assert.True(entry.ContainsKey("A"));
        Assert.Equal("v1", entry.GetByKey("A")?.Value);

        // null 键条目保留在快照里（全量扫描可见），只是进不了键索引。
        Assert.Equal(2, entry.Count);
        Assert.Equal(2, entry.GetSnapshot().Count);
    }

    [Fact]
    public async Task Null_key_policy_Throw_fails_the_whole_reload()
    {
        var loader = new SequenceLoader<SampleSwitch>(
            () => [Switch(null!, "broken"), Switch("A", "v1")]);
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleSwitch>(entity => entity
                .WithKey(item => item.Code)
                .WithLoader(_ => loader)
                .WithNullKeyPolicy(NullKeyPolicy.Throw));
            builder.WithWarmupOnStartup(false);
        });

        await using var provider = services.BuildServiceProvider();
        var entry = provider.GetRequiredService<IEntityCacheService>().Get<SampleSwitch>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => entry.ReloadAsync());

        Assert.Contains("returned null", exception.Message);
        Assert.Equal(0, entry.Version);
        Assert.Equal(0, entry.Count);
        Assert.Same(exception, entry.LastError);
    }

    [Fact]
    public async Task Reloaded_handlers_do_not_run_while_the_cache_lock_is_held()
    {
        var loader = new SequenceLoader<SampleSwitch>(() => [Switch("A", "v1")]);
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleSwitch>(entity => entity
                .WithKey(item => item.Code)
                .WithLoader(_ => loader));
            builder.WithWarmupOnStartup(false);
        });

        await using var provider = services.BuildServiceProvider();
        var entry = provider.GetRequiredService<IEntityCacheService>().Get<SampleSwitch>();

        using var handlerEntered = new ManualResetEventSlim();
        using var releaseHandler = new ManualResetEventSlim();
        entry.Reloaded += (_, _) =>
        {
            handlerEntered.Set();
            releaseHandler.Wait(TimeSpan.FromSeconds(10));
        };

        // 在后台线程刷新；加载器同步完成，回调会在刷新线程上执行。
        var reload = Task.Run(() => entry.ReloadAsync());
        try
        {
            await WaitUntilAsync(() => handlerEntered.IsSet);

            // 回调仍在执行时，缓存内部锁不应被持有。
            var observer = Task.Run(() => entry.IsStale);
            var completed = await Task.WhenAny(observer, Task.Delay(TimeSpan.FromSeconds(2)));

            Assert.Same(observer, completed);
            Assert.False(await observer);
        }
        finally
        {
            releaseHandler.Set();
        }

        await reload;
        Assert.Equal(1, entry.Version);
    }

    [Fact]
    public async Task A_throwing_Reloaded_handler_does_not_break_the_reload()
    {
        var loader = new FailingAfterFirstLoader<SampleSwitch>(Switch("A", "v1"));
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleSwitch>(entity => entity
                .WithKey(item => item.Code)
                .WithLoader(_ => loader));
            builder.WithWarmupOnStartup(false);
        });

        await using var provider = services.BuildServiceProvider();
        var entry = provider.GetRequiredService<IEntityCacheService>().Get<SampleSwitch>();
        var laterHandlerCalled = false;
        entry.Reloaded += (_, _) => throw new InvalidOperationException("handler boom");
        entry.Reloaded += (_, _) => laterHandlerCalled = true;

        // 成功路径：订阅者抛异常不影响刷新结果，也不影响后续订阅者。
        await entry.ReloadAsync();

        Assert.Equal(1, entry.Version);
        Assert.Equal(1, entry.Count);
        Assert.True(laterHandlerCalled);

        // 失败路径：调用方看到的仍是加载器的错误，而不是订阅者的异常。
        laterHandlerCalled = false;
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => entry.ReloadAsync());

        Assert.Contains("Simulated loader failure", exception.Message);
        Assert.True(laterHandlerCalled);
    }

    [Fact]
    public async Task Nested_tuple_lookups_are_flattened_before_comparison()
    {
        var loader = new SequenceLoader<SampleComposite>(
            () => [new SampleComposite { Group = "g", Code = "c", Value = "v1" }]);
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleComposite>(entity => entity
                .WithLoader(_ => loader));
            builder.WithWarmupOnStartup(false);
        });

        await using var provider = services.BuildServiceProvider();
        var entry = provider.GetRequiredService<IEntityCacheService>().Get<SampleComposite>();

        await entry.ReloadAsync();

        // 扁平元组走按下标直接比较的快路径。
        Assert.Equal("v1", entry.GetByKey(("g", "c"))?.Value);

        // 嵌套元组回退到展开比较：(("g","c")) 展开后仍是 ["g","c"]。
        Assert.Equal("v1", entry.GetByKey(ValueTuple.Create(("g", "c")))?.Value);
        Assert.True(entry.ContainsKey(ValueTuple.Create(("g", "c"))));
        Assert.Null(entry.GetByKey(ValueTuple.Create(("g", "other"))));
    }

    [Fact]
    public async Task Composite_key_lookups_do_not_allocate_on_the_read_path()
    {
        var loader = new SequenceLoader<SampleComposite>(
            () => [new SampleComposite { Group = "g", Code = "c", Value = "v1" }]);
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleComposite>(entity => entity
                .WithLoader(_ => loader));
            builder.WithWarmupOnStartup(false);
        });

        await using var provider = services.BuildServiceProvider();
        var entry = provider.GetRequiredService<IEntityCacheService>().Get<SampleComposite>();

        await entry.ReloadAsync();

        // 预先装箱，测量的就是查询本身，而不是元组装箱。
        var keys = new object[]
        {
            ("g", "c"),
            ("g", "missing"),
        };

        for (var i = 0; i < 1_000; i++)
        {
            _ = entry.GetByKey(keys[i % keys.Length]);
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            _ = entry.GetByKey(keys[i % keys.Length]);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        // 无嵌套键的复合键查询走零分配路径；旧实现每次要构造多个 List/数组。
        Assert.True(allocated < 10_000, $"1 万次复合键查询分配了 {allocated} 字节。");
    }

    [Fact]
    public async Task Snapshot_is_exposed_as_a_read_only_list()
    {
        var loader = new SequenceLoader<SampleSwitch>(() => [Switch("A", "v1")]);
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleSwitch>(entity => entity
                .WithKey(item => item.Code)
                .WithLoader(_ => loader));
            builder.WithWarmupOnStartup(false);
        });

        await using var provider = services.BuildServiceProvider();
        var entry = provider.GetRequiredService<IEntityCacheService>().Get<SampleSwitch>();

        await entry.ReloadAsync();

        var snapshot = entry.GetSnapshot();

        Assert.False(snapshot is SampleSwitch[]);
        Assert.Throws<NotSupportedException>(
            () => ((IList<SampleSwitch>)snapshot).Add(Switch("B", "v2")));
        Assert.Single(snapshot);
        Assert.Equal("A", snapshot[0].Code);
    }

    [Fact]
    public async Task Duplicate_key_policy_declared_on_the_attribute_is_applied()
    {
        // SamplePolicyEntity 的特性声明了 DuplicateKeyPolicy.KeepFirst（默认是保留最后一条）。
        var loader = new SequenceLoader<SamplePolicyEntity>(
            () =>
            [
                new SamplePolicyEntity { Code = "c", Value = "first" },
                new SamplePolicyEntity { Code = "c", Value = "last" },
            ]);
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SamplePolicyEntity>(entity => entity
                .WithLoader(_ => loader));
            builder.WithWarmupOnStartup(false);
        });

        await using var provider = services.BuildServiceProvider();
        var entry = provider.GetRequiredService<IEntityCacheService>().Get<SamplePolicyEntity>();

        await entry.ReloadAsync();

        Assert.Equal("first", entry.GetByKey("c")?.Value);
    }

    [Fact]
    public async Task Null_key_policy_declared_on_the_attribute_is_applied()
    {
        // SamplePolicyEntity 的特性声明了 NullKeyPolicy.Throw（默认是不索引但保留条目）。
        var loader = new SequenceLoader<SamplePolicyEntity>(
            () => [new SamplePolicyEntity { Code = null!, Value = "broken" }]);
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SamplePolicyEntity>(entity => entity
                .WithLoader(_ => loader));
            builder.WithWarmupOnStartup(false);
        });

        await using var provider = services.BuildServiceProvider();
        var entry = provider.GetRequiredService<IEntityCacheService>().Get<SamplePolicyEntity>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => entry.ReloadAsync());

        Assert.Contains("returned null", exception.Message);
        Assert.Equal(0, entry.Version);
        Assert.Equal(0, entry.Count);
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
