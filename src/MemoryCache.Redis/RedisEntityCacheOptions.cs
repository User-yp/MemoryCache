namespace MemoryCache.Redis;

/// <summary>
/// MemoryCache 的 Redis 共享快照配置。
/// </summary>
/// <remarks>
/// 默认语义：进程内快照是 L1，Redis 是 L2。L1 失效后的刷新先读 Redis；
/// Redis 没有（未加载或已过期）时才回源数据库，并把结果写回 Redis。
/// </remarks>
public sealed class RedisEntityCacheOptions
{
    private readonly Dictionary<Type, string> _entityKeys = [];

    /// <summary>
    /// 获取或设置 StackExchange.Redis 连接串。
    /// </summary>
    /// <remarks>默认 <c>127.0.0.1:6379</c>。</remarks>
    public string Configuration { get; set; } = "127.0.0.1:6379";

    /// <summary>
    /// 获取或设置快照键前缀。
    /// </summary>
    /// <remarks>默认 <c>memorycache:snapshot:</c>；键的其余部分默认取实体类型全名。</remarks>
    public string KeyPrefix { get; set; } = "memorycache:snapshot:";

    /// <summary>
    /// 获取或设置快照的生存时间。
    /// </summary>
    /// <remarks>
    /// 默认 30 分钟。到期后下次刷新会回源数据库——这是丢失广播通知时的兜底。
    /// </remarks>
    public TimeSpan EntryTimeToLive { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 获取或设置生存时间的随机抖动上限。
    /// </summary>
    /// <remarks>默认 5 分钟；实际 TTL = <see cref="EntryTimeToLive"/> + [0, 抖动)，避免同一时刻集体过期。</remarks>
    public TimeSpan TimeToLiveJitter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 获取或设置跨进程回源锁的持有时长。
    /// </summary>
    /// <remarks>默认 10 秒；用于避免 Redis 缺失时多个实例同时查库。</remarks>
    public TimeSpan RefreshLockTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 获取或设置未抢到回源锁时等待其它实例回填的时间。
    /// </summary>
    /// <remarks>默认 2 秒；等待后仍没有数据就自己回源（宁可多查一次库，也不要刷新失败）。</remarks>
    public TimeSpan RefreshLockWait { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 获取或设置失效广播的频道名。
    /// </summary>
    /// <remarks>默认 <c>memorycache:invalidate</c>。</remarks>
    public string InvalidationChannel { get; set; } = "memorycache:invalidate";

    /// <summary>
    /// 获取或设置当前实例标识，用于跳过自己发出的广播。
    /// </summary>
    /// <remarks>默认 <c>{机器名}:{进程号}</c>。</remarks>
    public string InstanceId { get; set; } = $"{Environment.MachineName}:{Environment.ProcessId}";

    /// <summary>
    /// 获取或设置 Redis 连接与命令超时。
    /// </summary>
    /// <remarks>默认 5 秒。</remarks>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 获取或设置实体过滤：返回 <c>false</c> 的实体不参与 Redis 共享快照（只用进程内缓存）。
    /// </summary>
    /// <remarks>默认所有实体都参与。</remarks>
    public Func<Type, bool>? EntityFilter { get; set; }

    /// <summary>
    /// 指定某个实体在 Redis 中使用的键名（不含前缀），用于多个系统共享同一份快照。
    /// </summary>
    /// <typeparam name="TEntity">实体类型。</typeparam>
    /// <param name="key">键名，例如 <c>job-config</c>。</param>
    public void MapEntityKey<TEntity>(string key)
        where TEntity : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _entityKeys[typeof(TEntity)] = key;
    }

    internal string BuildEntityKey(Type entityType)
        => _entityKeys.TryGetValue(entityType, out var key)
            ? key
            : entityType.FullName ?? entityType.Name;

    internal bool ShouldUseRedis(Type entityType)
        => EntityFilter?.Invoke(entityType) ?? true;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(InvalidationChannel);

        if (EntryTimeToLive <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(EntryTimeToLive), EntryTimeToLive, "must be greater than zero.");
        }

        if (TimeToLiveJitter < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(TimeToLiveJitter), TimeToLiveJitter, "must not be negative.");
        }

        if (RefreshLockTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RefreshLockTimeout), RefreshLockTimeout, "must be greater than zero.");
        }

        if (RefreshLockWait < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RefreshLockWait), RefreshLockWait, "must not be negative.");
        }
    }
}
