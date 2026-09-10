using MemoryCache.Abstractions;
using Microsoft.Extensions.Logging;

namespace MemoryCache.Redis;

/// <summary>
/// 两级缓存的加载器：先读共享快照（Redis），未命中或已过期时回源数据库并回填。
/// </summary>
/// <typeparam name="TEntity">实体类型。</typeparam>
internal sealed class RedisBackedEntityLoader<TEntity> : IEntityLoader<TEntity>
    where TEntity : class
{
    private readonly IEntityLoader<TEntity> _inner;
    private readonly IRedisEntitySnapshotStore<TEntity> _store;
    private readonly RedisEntityCacheOptions _options;
    private readonly ILogger? _logger;

    public RedisBackedEntityLoader(
        IEntityLoader<TEntity> inner,
        IRedisEntitySnapshotStore<TEntity> store,
        RedisEntityCacheOptions options,
        ILogger? logger = null)
    {
        _inner = inner;
        _store = store;
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyCollection<TEntity>> LoadAsync(CancellationToken cancellationToken)
    {
        var cached = await _store.TryReadAsync(cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            _logger?.LogDebug(
                "Loaded cache entity '{EntityName}' from the shared snapshot.",
                typeof(TEntity).Name);
            return cached.Items;
        }

        var lockResult = await _store.TryAcquireRefreshLockAsync(cancellationToken)
            .ConfigureAwait(false);

        if (lockResult == RedisRefreshLockResult.Unavailable)
        {
            // 共享存储不可用：退化为直接回源数据库。
            return await LoadFromSourceAsync(cancellationToken).ConfigureAwait(false);
        }

        if (lockResult == RedisRefreshLockResult.Busy)
        {
            // 其它实例正在回填：等一小会儿再读一次，仍没有就自己回源（宁可多查一次库）。
            if (_options.RefreshLockWait > TimeSpan.Zero)
            {
                await Task.Delay(_options.RefreshLockWait, cancellationToken).ConfigureAwait(false);
            }

            var refilled = await _store.TryReadAsync(cancellationToken).ConfigureAwait(false);
            if (refilled is not null)
            {
                return refilled.Items;
            }

            _logger?.LogWarning(
                "Another instance did not refill shared snapshot of cache entity '{EntityName}' " +
                "within {Wait}; loading from the database directly.",
                typeof(TEntity).Name,
                _options.RefreshLockWait);
            return await LoadFromSourceAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            // 双检：抢锁期间可能已有实例回填完成。
            var second = await _store.TryReadAsync(cancellationToken).ConfigureAwait(false);
            if (second is not null)
            {
                return second.Items;
            }

            var items = await LoadFromSourceAsync(cancellationToken).ConfigureAwait(false);
            await _store.WriteAsync(items, cancellationToken).ConfigureAwait(false);
            return items;
        }
        finally
        {
            await _store.ReleaseRefreshLockAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyCollection<TEntity>> LoadFromSourceAsync(
        CancellationToken cancellationToken)
        => await _inner.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Loader for cache entity '{typeof(TEntity).Name}' returned null.");
}
