namespace MemoryCache.Redis;

/// <summary>
/// 写入方使用的共享快照维护入口：数据库变更后回填 Redis 并广播失效。
/// </summary>
public interface IRedisEntitySnapshotMaintenance
{
    /// <summary>
    /// 变更数据库后调用：强制回源加载 → 写入共享快照（带 TTL）→ 刷新本进程缓存 →
    /// 广播其它实例失效。
    /// </summary>
    /// <typeparam name="TEntity">实体类型。</typeparam>
    /// <param name="cancellationToken">取消令牌。</param>
    Task RefreshFromSourceAsync<TEntity>(CancellationToken cancellationToken = default)
        where TEntity : class;

    /// <summary>
    /// 运行时类型版本，语义同 <see cref="RefreshFromSourceAsync{TEntity}(CancellationToken)"/>。
    /// </summary>
    /// <param name="entityType">实体类型。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task RefreshFromSourceAsync(Type entityType, CancellationToken cancellationToken = default);
}
