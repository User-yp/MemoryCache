namespace MemoryCache.Redis;

/// <summary>
/// 共享存储中的实体快照。
/// </summary>
/// <typeparam name="TEntity">实体类型。</typeparam>
public sealed class RedisEntitySnapshot<TEntity>
    where TEntity : class
{
    /// <summary>获取快照中的条目。</summary>
    public required IReadOnlyCollection<TEntity> Items { get; init; }

    /// <summary>获取该快照写入共享存储的时间。</summary>
    public required DateTimeOffset LoadedAt { get; init; }
}

/// <summary>
/// 抢占“回源加载”锁的结果。
/// </summary>
public enum RedisRefreshLockResult
{
    /// <summary>抢锁成功：由本次调用负责查库并回填共享存储。</summary>
    Acquired = 0,

    /// <summary>锁被其他实例持有：应等待其回填后重读共享存储。</summary>
    Busy = 1,

    /// <summary>共享存储不可用：直接回源数据库，不做等待与回填。</summary>
    Unavailable = 2,
}

/// <summary>
/// 实体快照在共享存储中的读写（默认实现基于 Redis）。
/// </summary>
/// <typeparam name="TEntity">实体类型。</typeparam>
/// <remarks>
/// 该接口是两级缓存的 L2 抽象：读失败按“未命中”处理（返回 <c>null</c>），
/// 写失败只记录日志，从而保证共享存储故障时组件降级为“只用进程内缓存 + 直接回源数据库”。
/// </remarks>
public interface IRedisEntitySnapshotStore<TEntity>
    where TEntity : class
{
    /// <summary>
    /// 尝试读取共享快照；不存在、已过期、格式不兼容或共享存储不可用时返回 <c>null</c>。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<RedisEntitySnapshot<TEntity>?> TryReadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 写入共享快照（带生存时间）。
    /// </summary>
    /// <param name="items">要写入的条目。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task WriteAsync(
        IReadOnlyCollection<TEntity> items,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 尝试抢占“回源加载”锁。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<RedisRefreshLockResult> TryAcquireRefreshLockAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 释放“回源加载”锁。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task ReleaseRefreshLockAsync(CancellationToken cancellationToken = default);
}
