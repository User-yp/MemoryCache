using System.Diagnostics;
using MemoryCache;
using MemoryCache.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace MemoryCache.Benchmark;

internal static class Program
{
    private const int ItemCount = 10_000;

    public static async Task<int> Main()
    {
        // 构造 10,000 行模拟“开关/配置”数据。
        var items = Enumerable.Range(0, ItemCount)
            .Select(index => new BenchmarkItem
            {
                Code = $"CFG-{index:00000}",
                Value = index.ToString(),
            })
            .ToArray();

        var loader = new StaticLoader(items);
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<BenchmarkItem>(entity => entity
                .WithKey(item => item.Code)
                .WithLoader(_ => loader));
            builder.WithWarmupOnStartup(false);
        });

        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<IEntityCacheService>().Get<BenchmarkItem>();

        // 预热：先加载一次，确保后续测量全部命中内存快照。
        await cache.ReloadAsync();
        Console.WriteLine(
            $"缓存条目数：{cache.Count}，Version={cache.Version}，" +
            $"加载耗时：{loader.LastLoadDuration.TotalMilliseconds:F1} ms");
        Console.WriteLine();

        Measure("GetByKey（按 Code 查 1,000,000 次）", 100, ItemCount, () =>
        {
            for (var i = 0; i < ItemCount; i++)
            {
                _ = cache.GetByKey($"CFG-{i:00000}");
            }
        });

        Measure("GetSnapshot（读快照 1,000,000 次）", 100_000, 10, () =>
        {
            for (var i = 0; i < 10; i++)
            {
                _ = cache.GetSnapshot();
            }
        });

        Measure("AsQueryable 全量过滤 1,000 次", 1_000, 1, () =>
        {
            _ = cache.AsQueryable().Where(item => item.Value == "5000").ToList();
        });

        return 0;
    }

    private static void Measure(
        string name,
        int iterations,
        int operationsPerIteration,
        Action action)
    {
        // JIT 预热一轮，避免把首次开销计入。
        action();

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            action();
        }

        stopwatch.Stop();
        var totalOperations = (long)iterations * operationsPerIteration;
        var operationsPerSecond = totalOperations / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.000_001);

        Console.WriteLine(
            $"{name,-45} 总耗时 {stopwatch.Elapsed.TotalMilliseconds,10:F1} ms，" +
            $"约 {operationsPerSecond / 1_000_000.0:F2} M ops/s");
    }

    [CacheEntity(KeyProperty = nameof(BenchmarkItem.Code))]
    private sealed class BenchmarkItem
    {
        public string Code { get; init; } = "";

        public string Value { get; init; } = "";
    }

    private sealed class StaticLoader(IReadOnlyCollection<BenchmarkItem> items)
        : IEntityLoader<BenchmarkItem>
    {
        public TimeSpan LastLoadDuration { get; private set; }

        public async Task<IReadOnlyCollection<BenchmarkItem>> LoadAsync(CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            await Task.Yield();
            stopwatch.Stop();
            LastLoadDuration = stopwatch.Elapsed;
            return items;
        }
    }
}
