namespace MemoryCache;

/// <summary>
/// 以受控并行度执行一组实体刷新任务。
/// </summary>
/// <remarks>
/// 与直接 <c>Task.WhenAll(source.Select(...))</c> 不同：并发的刷新任务会先在一道
/// 信号量上排队，因此并行度为 1 时是<b>严格串行</b>（第二个实体要等第一个实体加载完成
/// 才会开始），而不会在同一时刻把全部加载器一起启动。任一实体刷新失败不会阻止
/// 其他实体继续刷新。
/// </remarks>
internal static class ReloadThrottle
{
    internal static async Task RunAsync<TSource>(
        IEnumerable<TSource> sources,
        int maxDegreeOfParallelism,
        Func<TSource, CancellationToken, Task> reloadAsync,
        CancellationToken cancellationToken)
    {
        var items = sources as IReadOnlyList<TSource> ?? sources.ToArray();
        if (items.Count == 0)
        {
            return;
        }

        if (items.Count == 1)
        {
            await reloadAsync(items[0], cancellationToken).ConfigureAwait(false);
            return;
        }

        using var semaphore = new SemaphoreSlim(Math.Max(1, maxDegreeOfParallelism));
        var tasks = new Task[items.Count];

        for (var i = 0; i < items.Count; i++)
        {
            tasks[i] = RunOneAsync(items[i], reloadAsync, semaphore, cancellationToken);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task RunOneAsync<TSource>(
        TSource source,
        Func<TSource, CancellationToken, Task> reloadAsync,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await reloadAsync(source, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            semaphore.Release();
        }
    }
}
