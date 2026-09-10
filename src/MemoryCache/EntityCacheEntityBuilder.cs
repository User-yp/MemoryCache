using System.Reflection;
using MemoryCache.Abstractions;

namespace MemoryCache;

/// <summary>
/// 单个可缓存实体类型的流式（fluent）配置。
/// </summary>
/// <typeparam name="TEntity">被缓存的实体类型。</typeparam>
public sealed class EntityCacheEntityBuilder<TEntity>
    where TEntity : class
{
    private Func<TEntity, object?>? _keySelector;

    private Func<IServiceProvider, IEntityLoader<TEntity>>? _loaderFactory;

    private string? _name;

    internal EntityCacheEntityBuilder()
    {
    }

    internal string? KeyPropertyName { get; private set; }

    internal long CapacityWarningThreshold { get; private set; } = 50_000;

    internal double RefreshIntervalSeconds { get; private set; }

    internal InvalidationMode InvalidationMode { get; private set; } = InvalidationMode.ReloadInBackground;

    internal DuplicateKeyPolicy DuplicateKeyPolicy { get; private set; } =
        DuplicateKeyPolicy.LogWarningAndKeepLast;

    /// <summary>
    /// 配置用作缓存键的属性。
    /// </summary>
    /// <param name="propertyName"><typeparamref name="TEntity"/> 上的公共实例属性名；
    /// 复合键可用逗号分隔，例如 <c>"TenantId,Code"</c>。</param>
    /// <returns>当前构建器实例。</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="propertyName"/> 为 <c>null</c> 或空白字符串。
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <typeparamref name="TEntity"/> 不存在与 <paramref name="propertyName"/>
    /// 匹配的公共实例属性。
    /// </exception>
    public EntityCacheEntityBuilder<TEntity> WithKey(string propertyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName, nameof(propertyName));

        if (_keySelector is not null)
        {
            throw new InvalidOperationException(
                "The key has already been configured with a key selector delegate.");
        }

        if (!KeyPropertiesExist(propertyName))
        {
            throw new ArgumentException(
                $"Type '{typeof(TEntity).Name}' has no matching public instance property " +
                $"for key specification '{propertyName}'.",
                nameof(propertyName));
        }

        KeyPropertyName = propertyName;
        return this;
    }

    /// <summary>
    /// 配置一个从实体条目中提取缓存键的委托。
    /// </summary>
    /// <param name="keySelector">为单条实体计算缓存键的委托。</param>
    /// <returns>当前构建器实例。</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="keySelector"/> 为 <c>null</c>。
    /// </exception>
    public EntityCacheEntityBuilder<TEntity> WithKey(Func<TEntity, object?> keySelector)
    {
        ArgumentNullException.ThrowIfNull(keySelector);

        _keySelector = keySelector;
        KeyPropertyName = null;
        return this;
    }

    /// <summary>
    /// 配置用于加载该实体数据的显式加载器工厂。
    /// </summary>
    /// <param name="loaderFactory">
    /// 接收服务提供程序并返回 <see cref="IEntityLoader{TEntity}"/> 实例的工厂。
    /// </param>
    /// <returns>当前构建器实例。</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="loaderFactory"/> 为 <c>null</c>。
    /// </exception>
    public EntityCacheEntityBuilder<TEntity> WithLoader(
        Func<IServiceProvider, IEntityLoader<TEntity>> loaderFactory)
    {
        ArgumentNullException.ThrowIfNull(loaderFactory);

        _loaderFactory = loaderFactory;
        return this;
    }

    /// <summary>
    /// 配置加载过程中发现重复键时的处理方式。
    /// </summary>
    /// <param name="policy">重复键策略。</param>
    /// <returns>当前构建器实例。</returns>
    public EntityCacheEntityBuilder<TEntity> WithDuplicateKeyPolicy(DuplicateKeyPolicy policy)
    {
        DuplicateKeyPolicy = policy;
        return this;
    }

    /// <summary>
    /// 配置对当前实体调用服务级 <c>Invalidate</c> API 时的行为。
    /// </summary>
    /// <param name="mode">失效模式。</param>
    /// <returns>当前构建器实例。</returns>
    public EntityCacheEntityBuilder<TEntity> WithInvalidationMode(InvalidationMode mode)
    {
        InvalidationMode = mode;
        return this;
    }

    /// <summary>
    /// 配置当前实体的定时刷新周期。
    /// </summary>
    /// <param name="interval">
    /// 刷新周期；<see cref="TimeSpan.Zero"/> 表示不启用定时刷新。
    /// </param>
    /// <returns>当前构建器实例。</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="interval"/> 为负数。
    /// </exception>
    public EntityCacheEntityBuilder<TEntity> WithRefreshInterval(TimeSpan interval)
    {
        if (interval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "must not be negative.");
        }

        RefreshIntervalSeconds = interval.TotalSeconds;
        return this;
    }

    internal EntityCacheEntityBuilder<TEntity> ApplyAttribute(CacheEntityAttribute attribute)
    {
        if (_name is null && !string.IsNullOrWhiteSpace(attribute.Name))
        {
            _name = attribute.Name;
        }

        if (KeyPropertyName is null
            && _keySelector is null
            && !string.IsNullOrWhiteSpace(attribute.KeyProperty))
        {
            KeyPropertyName = attribute.KeyProperty;
        }

        CapacityWarningThreshold = attribute.CapacityWarningThreshold;
        RefreshIntervalSeconds = attribute.RefreshIntervalSeconds;
        return this;
    }

    internal EntityCacheRegistration<TEntity> Build()
    {
        if (KeyPropertyName is not null && !KeyPropertiesExist(KeyPropertyName))
        {
            throw new InvalidOperationException(
                $"Type '{typeof(TEntity).Name}' has no matching public instance property " +
                $"for key specification '{KeyPropertyName}', " +
                "which was configured as its cache key property.");
        }

        return new EntityCacheRegistration<TEntity>
        {
            EntityType = typeof(TEntity),
            Name = _name ?? typeof(TEntity).Name,
            KeyPropertyName = KeyPropertyName ?? FindConventionalKeyProperty(),
            LoaderFactory = _loaderFactory,
            KeySelector = _keySelector,
            CapacityWarningThreshold = CapacityWarningThreshold,
            RefreshIntervalSeconds = RefreshIntervalSeconds,
            InvalidationMode = InvalidationMode,
            DuplicateKeyPolicy = DuplicateKeyPolicy,
        };
    }

    private static string? FindConventionalKeyProperty()
    {
        return typeof(TEntity).GetProperty("Id", BindingFlags.Public | BindingFlags.Instance) is null
            ? null
            : "Id";
    }

    private static bool KeyPropertiesExist(string propertyNames)
    {
        var names = propertyNames.Split(
            ',',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        return names.Length > 0
            && names.All(name =>
                typeof(TEntity).GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is not null);
    }
}
