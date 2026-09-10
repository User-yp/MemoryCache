using MemoryCache.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace MemoryCache.Tests;

public sealed class CacheServiceHostingTests
{
    [Fact]
    public async Task Default_configuration_registers_warmup_and_loads_on_start()
    {
        var loader = new SequenceLoader<SampleSwitch>(() => [CreateSwitch("A", "v1")]);
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleSwitch>(entity => entity
                .WithKey(item => item.Code)
                .WithLoader(_ => loader));
        });

        await using var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToArray();

        Assert.Single(hostedServices);
        Assert.Equal("EntityCacheWarmupHostedService", hostedServices[0].GetType().Name);

        await StartAllAsync(hostedServices);
        try
        {
            var entry = provider.GetRequiredService<IEntityCacheService>().Get<SampleSwitch>();
            Assert.Equal(1, entry.Version);
            Assert.Equal(1, entry.Count);
            Assert.Equal(1, loader.LoadCount);
        }
        finally
        {
            await StopAllAsync(hostedServices);
        }
    }

    [Fact]
    public async Task Disabling_warmup_skips_automatic_load()
    {
        var loader = new SequenceLoader<SampleSwitch>(() => [CreateSwitch("A", "v1")]);
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleSwitch>(entity => entity
                .WithKey(item => item.Code)
                .WithLoader(_ => loader));
            builder.WithWarmupOnStartup(false);
        });

        await using var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToArray();

        Assert.Empty(hostedServices);
        await StartAllAsync(hostedServices);
        Assert.Equal(0, loader.LoadCount);
    }

    [Fact]
    public async Task Warmup_failure_with_continue_policy_keeps_application_running()
    {
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleSwitch>(entity => entity
                .WithKey(item => item.Code)
                .WithLoader(_ => new AlwaysFailingLoader<SampleSwitch>()));
        });

        await using var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToArray();

        await StartAllAsync(hostedServices);
        try
        {
            var entry = provider.GetRequiredService<IEntityCacheService>().Get<SampleSwitch>();
            Assert.NotNull(entry.LastError);
            Assert.Equal(0, entry.Count);
        }
        finally
        {
            await StopAllAsync(hostedServices);
        }
    }

    [Fact]
    public async Task Warmup_failure_with_throw_policy_propagates()
    {
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleSwitch>(entity => entity
                .WithKey(item => item.Code)
                .WithLoader(_ => new AlwaysFailingLoader<SampleSwitch>()));
            builder.WithStartupFailurePolicy(StartupFailurePolicy.Throw);
        });

        await using var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToArray();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartAllAsync(hostedServices));
        Assert.Contains("Simulated loader failure", exception.Message);

        await StopAllAsync(hostedServices);
    }

    [Fact]
    public async Task Warmup_timeout_with_continue_policy_keeps_application_running()
    {
        var loader = new GateLoader<SampleSwitch>(CreateSwitch("A", "v1"));
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleSwitch>(entity => entity
                .WithKey(item => item.Code)
                .WithLoader(_ => loader));
            builder.WithWarmupTimeout(TimeSpan.FromMilliseconds(100));
        });

        await using var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToArray();
        var entry = provider.GetRequiredService<IEntityCacheService>().Get<SampleSwitch>();

        // 默认策略为 Continue：预热超时只记录告警，不阻止应用启动。
        await StartAllAsync(hostedServices);
        try
        {
            // 超时会真正取消取数，缓存保持未加载状态，且不算加载失败。
            await WaitUntilAsync(() => loader.ObservedCancellation, timeoutMilliseconds: 10_000);
            Assert.Equal(0, entry.Version);
            Assert.Equal(0, entry.Count);
            Assert.Null(entry.LastError);
        }
        finally
        {
            await StopAllAsync(hostedServices);
        }
    }

    [Fact]
    public async Task Warmup_timeout_with_throw_policy_fails_start()
    {
        var loader = new GateLoader<SampleSwitch>(CreateSwitch("A", "v1"));
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleSwitch>(entity => entity
                .WithKey(item => item.Code)
                .WithLoader(_ => loader));
            builder.WithWarmupTimeout(TimeSpan.FromMilliseconds(100));
            builder.WithStartupFailurePolicy(StartupFailurePolicy.Throw);
        });

        await using var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToArray();

        var exception = await Assert.ThrowsAsync<TimeoutException>(
            () => StartAllAsync(hostedServices));
        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);

        await WaitUntilAsync(() => loader.ObservedCancellation, timeoutMilliseconds: 10_000);

        var entry = provider.GetRequiredService<IEntityCacheService>().Get<SampleSwitch>();
        Assert.Equal(0, entry.Version);
        Assert.Null(entry.LastError);

        await StopAllAsync(hostedServices);
    }

    [Fact]
    public async Task Warmup_is_aborted_when_the_host_shuts_down()
    {
        var loader = new GateLoader<SampleSwitch>(CreateSwitch("A", "v1"));
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleSwitch>(entity => entity
                .WithKey(item => item.Code)
                .WithLoader(_ => loader));
        });

        await using var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToArray();
        var entry = provider.GetRequiredService<IEntityCacheService>().Get<SampleSwitch>();

        using var shutdown = new CancellationTokenSource();
        var start = hostedServices[0].StartAsync(shutdown.Token);
        await WaitUntilAsync(() => loader.LoadCount == 1, timeoutMilliseconds: 10_000);

        shutdown.Cancel();

        // 宿主取消不属于预热失败，会照常向上传播。
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => start);

        // 在途加载被真正中断。
        await WaitUntilAsync(() => loader.ObservedCancellation, timeoutMilliseconds: 10_000);
        Assert.Equal(0, entry.Version);
        Assert.Null(entry.LastError);

        await StopAllAsync(hostedServices);
    }

    [Fact]
    public async Task Periodic_refresh_reloads_entities_automatically()
    {
        var loader = new SequenceLoader<SampleSwitch>(() => [CreateSwitch("A", "v1")]);
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleSwitch>(entity => entity
                .WithKey(item => item.Code)
                .WithLoader(_ => loader)
                .WithRefreshInterval(TimeSpan.FromMilliseconds(150)));
            builder.WithWarmupOnStartup(false);
        });

        await using var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToArray();

        Assert.Single(hostedServices);
        Assert.Equal("EntityCachePeriodicRefresher", hostedServices[0].GetType().Name);

        await StartAllAsync(hostedServices);
        try
        {
            await WaitUntilAsync(() => loader.LoadCount >= 2, timeoutMilliseconds: 10_000);
        }
        finally
        {
            await StopAllAsync(hostedServices);
        }
    }

    [Fact]
    public async Task Periodic_refresh_recovers_after_a_failed_load()
    {
        var loader = new FailingAfterFirstLoader<SampleSwitch>(CreateSwitch("A", "v1"));
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleSwitch>(entity => entity
                .WithKey(item => item.Code)
                .WithLoader(_ => loader)
                .WithRefreshInterval(TimeSpan.FromMilliseconds(150)));
            builder.WithWarmupOnStartup(false);
        });

        await using var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToArray();

        await StartAllAsync(hostedServices);
        try
        {
            var entry = provider.GetRequiredService<IEntityCacheService>().Get<SampleSwitch>();

            await WaitUntilAsync(() => entry.LastError is not null, timeoutMilliseconds: 10_000);
            Assert.Equal(2, loader.LoadCount);

            await WaitUntilAsync(
                () => entry.LastError is null && loader.LoadCount >= 3,
                timeoutMilliseconds: 10_000);
        }
        finally
        {
            await StopAllAsync(hostedServices);
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

    private static SampleSwitch CreateSwitch(string code, string value)
        => new()
        {
            Code = code,
            Value = value,
        };

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline)
            {
                throw new TimeoutException("Condition was not met within the timeout.");
            }

            await Task.Delay(20);
        }
    }
}
