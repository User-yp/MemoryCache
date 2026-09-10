using MemoryCache.Abstractions;

namespace MemoryCache;

/// <summary>
/// 服务注册表使用的非泛型注册记录。
/// </summary>
internal abstract class EntityCacheRegistration
{
    public required Type EntityType { get; init; }

    public required string Name { get; init; }

    public string? KeyPropertyName { get; init; }

    public long CapacityWarningThreshold { get; init; }

    public double RefreshIntervalSeconds { get; init; }

    public InvalidationMode InvalidationMode { get; init; }

    public DuplicateKeyPolicy DuplicateKeyPolicy { get; init; }

    public NullKeyPolicy NullKeyPolicy { get; init; }

    /// <summary>
    /// 获取一个值，指示加载器必须从 DI 解析，而不是使用显式加载器工厂。
    /// </summary>
    public abstract bool UsesDiLoader { get; }

    /// <summary>
    /// 为本次注册创建缓存项。
    /// </summary>
    /// <param name="serviceProvider">根服务提供程序。</param>
    /// <param name="lifetimeToken">
    /// 缓存服务的生命周期令牌；服务被释放时取消，用于中止后台刷新。
    /// </param>
    public abstract IEntityCache CreateEntry(
        IServiceProvider serviceProvider,
        CancellationToken lifetimeToken);
}

internal sealed class EntityCacheRegistration<TEntity> : EntityCacheRegistration
    where TEntity : class
{
    public Func<IServiceProvider, IEntityLoader<TEntity>>? LoaderFactory { get; init; }

    public Func<TEntity, object?>? KeySelector { get; init; }

    public TimeSpan? LoadTimeout { get; init; }

    public override bool UsesDiLoader => LoaderFactory is null;

    public override IEntityCache CreateEntry(
        IServiceProvider serviceProvider,
        CancellationToken lifetimeToken)
        => new EntityCache<TEntity>(serviceProvider, this, lifetimeToken);
}
