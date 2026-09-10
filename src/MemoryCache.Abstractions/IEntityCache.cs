namespace MemoryCache.Abstractions;

/// <summary>
/// 单个实体类型的进程内缓存项。
/// </summary>
/// <typeparam name="TEntity">被缓存的实体类型。</typeparam>
/// <remarks>
/// <para>
/// 读取基于不可变快照、无锁进行，因此永远不会触达底层数据源。刷新为异步操作，
/// 并且做了单飞（single-flight）合并：并发的刷新请求共享同一次加载任务。
/// </para>
/// <para>
/// 快照按只读使用：条目以引用方式保存，调用方不得修改它们，
/// 否则缓存可能对外提供被污染的数据。
/// </para>
/// </remarks>
public interface IEntityCache<TEntity>
    where TEntity : class
{
    /// <summary>
    /// 获取缓存项创建以来成功刷新的次数。
    /// </summary>
    long Version { get; }

    /// <summary>
    /// 获取当前快照中的条目数量。
    /// </summary>
    int Count { get; }

    /// <summary>
    /// 获取一个值，指示缓存项是否已被标记为失效但尚未成功刷新。
    /// </summary>
    bool IsStale { get; }

    /// <summary>
    /// 获取最近一次成功加载的时间；若从未成功加载过则为 <c>null</c>。
    /// </summary>
    DateTimeOffset? LoadedAt { get; }

    /// <summary>
    /// 获取最近一次刷新失败的错误；若最近一次刷新成功或尚未尝试过刷新则为
    /// <c>null</c>。
    /// </summary>
    Exception? LastError { get; }

    /// <summary>
    /// 以只读列表形式返回当前快照。
    /// </summary>
    IReadOnlyList<TEntity> GetSnapshot();

    /// <summary>
    /// 将当前快照暴露为 LINQ 内存查询入口。
    /// </summary>
    IQueryable<TEntity> AsQueryable();

    /// <summary>
    /// 当前快照包含指定键的条目时返回 <c>true</c>。
    /// </summary>
    /// <param name="key">条目的缓存键。</param>
    /// <exception cref="NotSupportedException">
    /// 实体未配置缓存键。
    /// </exception>
    bool ContainsKey(object key);

    /// <summary>
    /// 尝试获取与 <paramref name="key"/> 关联的条目。
    /// </summary>
    /// <param name="key">条目的缓存键。</param>
    /// <param name="value">获取到的条目；未找到时为 <c>null</c>。</param>
    /// <returns>键存在时返回 <c>true</c>；否则返回 <c>false</c>。</returns>
    /// <exception cref="NotSupportedException">
    /// 实体未配置缓存键。
    /// </exception>
    bool TryGetValue(object key, out TEntity? value);

    /// <summary>
    /// 返回与 <paramref name="key"/> 关联的条目；快照中不存在该键时返回
    /// <c>null</c>。
    /// </summary>
    /// <param name="key">条目的缓存键。</param>
    /// <exception cref="NotSupportedException">
    /// 实体未配置缓存键。
    /// </exception>
    TEntity? GetByKey(object key);

    /// <summary>
    /// 无论数据是否新鲜，强制刷新该实体类型。
    /// </summary>
    /// <param name="cancellationToken">用于取消刷新的令牌。</param>
    /// <returns>表示刷新操作的任务。</returns>
    Task ReloadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 仅当缓存项缺失、已失效或上次刷新失败时才重新加载该实体类型；
    /// 否则保留当前快照。
    /// </summary>
    /// <param name="cancellationToken">用于取消刷新的令牌。</param>
    /// <returns>表示新鲜度检查及可选刷新操作的任务。</returns>
    Task EnsureFreshAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 该实体类型每次刷新尝试（无论成功或失败）完成后触发。
    /// </summary>
    event EventHandler<EntityCacheReloadedEventArgs<TEntity>>? Reloaded;
}
