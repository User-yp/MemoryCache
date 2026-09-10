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

        // 复合键（GROUP + CODE）数据集。
        var compositeItems = Enumerable.Range(0, ItemCount)
            .Select(index => new BenchmarkCompositeItem
            {
                Group = $"G{index % 100:00}",
                Code = $"CFG-{index:00000}",
                Value = index.ToString(),
            })
            .ToArray();

        var loader = new StaticLoader<BenchmarkItem>(items);
        var compositeLoader = new StaticLoader<BenchmarkCompositeItem>(compositeItems);

        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<BenchmarkItem>(entity => entity
                .WithKey(item => item.Code)
                .WithLoader(_ => loader));
            builder.AddEntity<BenchmarkCompositeItem>(entity => entity
                .WithLoader(_ => compositeLoader));
            builder.WithWarmupOnStartup(false);
        });

        await using var provider = services.BuildServiceProvider();
        var cacheService = provider.GetRequiredService<IEntityCacheService>();
        var cache = cacheService.Get<BenchmarkItem>();
        var compositeCache = cacheService.Get<BenchmarkCompositeItem>();

        // 预热：先加载一次，确保后续测量全部命中内存快照。
        await cache.ReloadAsync();
        await compositeCache.ReloadAsync();
        Console.WriteLine(
            $"缓存条目数：{cache.Count}（单键）/{compositeCache.Count}（复合键），" +
            $"Version={cache.Version}/{compositeCache.Version}，" +
            $"加载耗时：{loader.LastLoadDuration.TotalMilliseconds:F1} ms");
        Console.WriteLine();

        // 键预先构造好，测量的是纯查询开销（不含字符串拼接与元组装箱）。
        var singleKeys = Enumerable.Range(0, ItemCount)
            .Select(index => (object)$"CFG-{index:00000}")
            .ToArray();
        var compositeKeys = Enumerable.Range(0, ItemCount)
            .Select(index => (object)($"G{index % 100:00}", $"CFG-{index:00000}"))
            .ToArray();

        Measure("GetByKey 单键（100 万次）", 100, ItemCount, () =>
        {
            for (var i = 0; i < ItemCount; i++)
            {
                _ = cache.GetByKey(singleKeys[i]);
            }
        });

        Measure("GetByKey 复合键元组（100 万次）", 100, ItemCount, () =>
        {
            for (var i = 0; i < ItemCount; i++)
            {
                _ = compositeCache.GetByKey(compositeKeys[i]);
            }
        });

        Measure("GetSnapshot（读快照 100 万次）", 100_000, 10, () =>
        {
            for (var i = 0; i < 10; i++)
            {
                _ = cache.GetSnapshot();
            }
        });

        Measure("AsQueryable 全量过滤（1,000 次）", 1_000, 1, () =>
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

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            action();
        }

        stopwatch.Stop();
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var totalOperations = (long)iterations * operationsPerIteration;
        var microsecondsPerOperation =
            stopwatch.Elapsed.TotalMilliseconds * 1_000 / totalOperations;
        var bytesPerOperation = allocatedBytes / (double)totalOperations;

        Console.WriteLine(
            $"{name,-32} 总耗时 {stopwatch.Elapsed.TotalMilliseconds,9:F1} ms，" +
            $"约 {microsecondsPerOperation,10:F3} µs/次，" +
            $"分配 {bytesPerOperation,7:F1} B/次");
    }

    [CacheEntity(KeyProperty = nameof(BenchmarkItem.Code))]
    private sealed class BenchmarkItem
    {
        public string Code { get; init; } = "";

        public string Value { get; init; } = "";
    }

    [CacheEntity(KeyProperty = "Group,Code")]
    private sealed class BenchmarkCompositeItem
    {
        public string Group { get; init; } = "";

        public string Code { get; init; } = "";

        public string Value { get; init; } = "";
    }

    private sealed class StaticLoader<TEntity>(IReadOnlyCollection<TEntity> items)
        : IEntityLoader<TEntity>
        where TEntity : class
    {
        public TimeSpan LastLoadDuration { get; private set; }

        public async Task<IReadOnlyCollection<TEntity>> LoadAsync(CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            await Task.Yield();
            stopwatch.Stop();
            LastLoadDuration = stopwatch.Elapsed;
            return items;
        }
    }
}
