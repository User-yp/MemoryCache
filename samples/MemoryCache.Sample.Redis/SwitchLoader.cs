using MemoryCache.Abstractions;

namespace MemoryCache.Sample.Redis;

/// <summary>
/// 从（假）数据库全量加载开关表；真实场景中这里换成 EF Core / Dapper / MySqlConnector。
/// </summary>
public sealed class SwitchLoader(FakeSwitchDatabase database) : IEntityLoader<FeatureSwitch>
{
    private int _loadCount;

    /// <summary>
    /// 获取加载器被调用的次数：它能说明缓存（L1/L2）挡掉了多少次数据库访问。
    /// </summary>
    /// <remarks>
    /// 注意：启用 Redis 共享快照后，这个加载器就是“最内层”的原始加载器，
    /// 只有 L2 未命中时才会真正执行。
    /// </remarks>
    public int LoadCount => Volatile.Read(ref _loadCount);

    public async Task<IReadOnlyCollection<FeatureSwitch>> LoadAsync(
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _loadCount);

        // 模拟一次数据库往返，便于在输出里看出“这次是否真的查了库”。
        await Task.Delay(TimeSpan.FromMilliseconds(80), cancellationToken);

        return database.QueryAll();
    }
}
