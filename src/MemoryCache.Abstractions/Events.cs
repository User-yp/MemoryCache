namespace MemoryCache.Abstractions;

/// <summary>
/// 为 <see cref="IEntityCache{TEntity}.Reloaded"/> 事件提供数据。
/// </summary>
/// <typeparam name="TEntity">被缓存的实体类型。</typeparam>
public sealed class EntityCacheReloadedEventArgs<TEntity> : EventArgs
    where TEntity : class
{
    /// <summary>
    /// 获取本次刷新尝试之前的版本号。
    /// </summary>
    public required long PreviousVersion { get; init; }

    /// <summary>
    /// 获取本次刷新尝试之后的版本号。失败时它等于
    /// <see cref="PreviousVersion"/>，因为旧快照被保留。
    /// </summary>
    public required long NewVersion { get; init; }

    /// <summary>
    /// 获取一个值，指示本次刷新尝试是否失败。
    /// </summary>
    public required bool Failed { get; init; }

    /// <summary>
    /// 获取失败刷新尝试的错误；成功时为 <c>null</c>。
    /// </summary>
    public Exception? Error { get; init; }
}

/// <summary>
/// 为 <see cref="IEntityCacheService.Invalidated"/> 事件提供数据。
/// </summary>
public sealed class EntityCacheInvalidatedEventArgs : EventArgs
{
    /// <summary>
    /// 获取缓存项被失效的实体类型。
    /// </summary>
    public required Type EntityType { get; init; }

    /// <summary>
    /// 获取失效通知被触发的时间。
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }
}
