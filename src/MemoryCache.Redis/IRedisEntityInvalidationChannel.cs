namespace MemoryCache.Redis;

/// <summary>
/// 跨进程失效广播消息。
/// </summary>
public sealed class RedisEntityInvalidationMessage
{
    /// <summary>获取实体类型的程序集限定名。</summary>
    public required string EntityType { get; init; }

    /// <summary>获取发起广播的实例标识。</summary>
    public required string Origin { get; init; }

    /// <summary>获取广播时间。</summary>
    public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// 跨进程失效广播通道：写入方回填共享快照后通知其它实例刷新各自的进程内快照。
/// </summary>
public interface IRedisEntityInvalidationChannel
{
    /// <summary>
    /// 广播“某个实体的数据已变化”。
    /// </summary>
    /// <param name="entityType">发生变化的实体类型。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 广播发送失败只记录告警：其它实例会在 Redis TTL 到期或自身定时刷新时收敛。
    /// </remarks>
    Task PublishAsync(Type entityType, CancellationToken cancellationToken = default);
}
