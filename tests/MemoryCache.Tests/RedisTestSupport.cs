using MemoryCache.Redis;
using StackExchange.Redis;

namespace MemoryCache.Tests;

/// <summary>可注入的假共享存储，用于不依赖 Redis 的单元测试。</summary>
internal sealed class FakeRedisEntitySnapshotStore<TEntity> : IRedisEntitySnapshotStore<TEntity>
    where TEntity : class
{
    private RedisEntitySnapshot<TEntity>? _snapshot;

    public List<IReadOnlyCollection<TEntity>> Writes { get; } = [];

    public int ReadCount { get; private set; }

    public int LockAcquireCount { get; private set; }

    public int ReleaseCount { get; private set; }

    public RedisRefreshLockResult LockResult { get; set; } = RedisRefreshLockResult.Acquired;

    /// <summary>按第几次读取返回不同的结果（从 1 开始计数）；为 null 时返回当前快照。</summary>
    public Func<int, RedisEntitySnapshot<TEntity>?>? ReadHandler { get; set; }

    public void Seed(IReadOnlyCollection<TEntity> items)
        => _snapshot = new RedisEntitySnapshot<TEntity>
        {
            Items = items,
            LoadedAt = DateTimeOffset.UtcNow,
        };

    public Task<RedisEntitySnapshot<TEntity>?> TryReadAsync(
        CancellationToken cancellationToken = default)
    {
        ReadCount++;
        return Task.FromResult(ReadHandler?.Invoke(ReadCount) ?? _snapshot);
    }

    public Task WriteAsync(
        IReadOnlyCollection<TEntity> items,
        CancellationToken cancellationToken = default)
    {
        Writes.Add(items);
        Seed(items);
        return Task.CompletedTask;
    }

    public Task<RedisRefreshLockResult> TryAcquireRefreshLockAsync(
        CancellationToken cancellationToken = default)
    {
        LockAcquireCount++;
        return Task.FromResult(LockResult);
    }

    public Task ReleaseRefreshLockAsync(CancellationToken cancellationToken = default)
    {
        ReleaseCount++;
        return Task.CompletedTask;
    }
}

/// <summary>记录广播内容的假失效通道。</summary>
internal sealed class FakeInvalidationChannel : IRedisEntityInvalidationChannel
{
    public List<Type> Published { get; } = [];

    public Task PublishAsync(Type entityType, CancellationToken cancellationToken = default)
    {
        Published.Add(entityType);
        return Task.CompletedTask;
    }
}

/// <summary>真实 Redis 集成测试的可用性与命名辅助。</summary>
internal static class RedisTestSupport
{
    /// <summary>
    /// 集成测试使用的 Redis 连接串；可用环境变量 <c>MEMORY_CACHE_REDIS</c> 覆盖（默认本机）。
    /// </summary>
    public static string Connection { get; } =
        Environment.GetEnvironmentVariable("MEMORY_CACHE_REDIS") is { Length: > 0 } configured
            ? configured
            : "127.0.0.1:6379";

    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            var options = ConfigurationOptions.Parse(Connection);
            options.AbortOnConnectFail = false;
            options.ConnectTimeout = 2000;
            options.SyncTimeout = 2000;

            using var connection = ConnectionMultiplexer.Connect(options);
            return connection.GetDatabase().Ping() >= TimeSpan.Zero;
        }
        catch
        {
            return false;
        }
    });

    public static bool IsAvailable => Available.Value;

    /// <summary>为每个测试生成隔离的键前缀。</summary>
    public static string NewKeyPrefix() => $"memorycache:test:{Guid.NewGuid():N}:";
}
