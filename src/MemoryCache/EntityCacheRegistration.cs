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

    /// <summary>
    /// 获取一个值，指示加载器必须从 DI 解析，而不是使用显式加载器工厂。
    /// </summary>
    public abstract bool UsesDiLoader { get; }

    /// <summary>
    /// 为本次注册创建缓存项。
    /// </summary>
    public abstract IEntityCache CreateEntry(IServiceProvider serviceProvider);
}

internal sealed class EntityCacheRegistration<TEntity> : EntityCacheRegistration
    where TEntity : class
{
    public Func<IServiceProvider, IEntityLoader<TEntity>>? LoaderFactory { get; init; }

    public Func<TEntity, object?>? KeySelector { get; init; }

    public override bool UsesDiLoader => LoaderFactory is null;

    public override IEntityCache CreateEntry(IServiceProvider serviceProvider)
        => new EntityCache<TEntity>(serviceProvider, this);
}
