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
}
