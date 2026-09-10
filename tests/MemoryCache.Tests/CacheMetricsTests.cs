using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using MemoryCache;
using MemoryCache.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MemoryCache.Tests;

public sealed class CacheMetricsTests
{
    [Fact]
    public async Task Reload_records_counters_and_duration_histogram()
    {
        var counters = new ConcurrentQueue<(string Name, long Value)>();
        var histograms = new ConcurrentQueue<(string Name, double Value)>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "MemoryCache")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            counters.Enqueue((instrument.Name, measurement)));
        listener.SetMeasurementEventCallback<double>((instrument, measurement, _, _) =>
            histograms.Enqueue((instrument.Name, measurement)));
        listener.Start();

        var loader = new SequenceLoader<SampleSwitch>(() => [CreateSwitch("A", "v1")]);
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

        Assert.Contains(
            counters,
            measurement => measurement.Name == "memorycache.reloads_total" && measurement.Value == 1);
        Assert.Contains(
            histograms,
            measurement => measurement.Name == "memorycache.reload_duration_seconds");
    }

    private static SampleSwitch CreateSwitch(string code, string value)
        => new()
        {
            Code = code,
            Value = value,
        };

    [Fact]
    public void Each_container_gets_its_own_meter_without_duplicate_gauges()
    {
        var testThreadId = Environment.CurrentManagedThreadId;
        var published = new List<(Meter Meter, string Name)>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            // 只统计本线程创建的仪表：其它测试会在各自的线程上并行创建 Meter。
            if (instrument.Meter.Name == "MemoryCache"
                && Environment.CurrentManagedThreadId == testThreadId)
            {
                published.Add((instrument.Meter, instrument.Name));
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.Start();

        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleSwitch>(entity => entity
                .WithKey(item => item.Code)
                .WithLoader(_ => new SequenceLoader<SampleSwitch>(() => [CreateSwitch("A", "v1")])));
            builder.WithWarmupOnStartup(false);
        });

        using (var first = services.BuildServiceProvider())
        {
            _ = first.GetRequiredService<IEntityCacheService>();
        }

        using (var second = services.BuildServiceProvider())
        {
            _ = second.GetRequiredService<IEntityCacheService>();
        }

        // 两个容器各持一份 Meter，且同一个 Meter 上不会把仪表注册两遍
        // （旧实现里两个容器共享同一个 Meter，gauge 会被注册两遍）。
        var meters = published.Select(entry => entry.Meter).Distinct().ToList();
        Assert.Equal(2, meters.Count);
        Assert.All(
            meters,
            meter =>
            {
                Assert.Equal(
                    1,
                    published.Count(entry =>
                        ReferenceEquals(entry.Meter, meter)
                        && entry.Name == "memorycache.snapshot_items"));
                Assert.Equal(
                    1,
                    published.Count(entry =>
                        ReferenceEquals(entry.Meter, meter)
                        && entry.Name == "memorycache.registered_types"));
            });
    }
}
