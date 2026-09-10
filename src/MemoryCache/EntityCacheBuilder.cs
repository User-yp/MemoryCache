using System.Reflection;
using System.Runtime.ExceptionServices;
using MemoryCache.Abstractions;

namespace MemoryCache;

/// <summary>
/// 在缓存服务加入 DI 之前收集各缓存实体的注册信息。
/// </summary>
public sealed class EntityCacheBuilder
{
    private readonly List<EntityCacheRegistration> _registrations = [];

    private readonly EntityCacheServiceOptions _serviceOptions = new();

    internal EntityCacheBuilder()
    {
    }

    internal IReadOnlyList<EntityCacheRegistration> Registrations => _registrations;

    internal EntityCacheServiceOptions ServiceOptions => _serviceOptions;

    /// <summary>
    /// 注册一个实体类型，可附上流式（fluent）配置。
    /// </summary>
    /// <typeparam name="TEntity">被缓存的实体类型。</typeparam>
    /// <param name="configure">可选的配置回调。</param>
    /// <returns>返回实体构建器，便于链式配置。</returns>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TEntity"/> 已被注册。
    /// </exception>
    public EntityCacheEntityBuilder<TEntity> AddEntity<TEntity>(
        Action<EntityCacheEntityBuilder<TEntity>>? configure = null)
        where TEntity : class
    {
        var entityBuilder = new EntityCacheEntityBuilder<TEntity>();

        var attribute = typeof(TEntity).GetCustomAttribute<CacheEntityAttribute>(inherit: false);
        if (attribute is not null)
        {
            entityBuilder.ApplyAttribute(attribute);
        }

        configure?.Invoke(entityBuilder);

        var registration = entityBuilder.Build();
        AddRegistration(registration);
        return entityBuilder;
    }

    /// <summary>
    /// 注册某个程序集中所有标记了 <see cref="CacheEntityAttribute"/> 的类。
    /// 它们的加载器以 <see cref="IEntityLoader{TEntity}"/> 的形式从 DI 解析。
    /// </summary>
    /// <param name="assembly">待扫描的程序集。</param>
    public void ScanAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ScanAssembliesCore([assembly]);
    }

    /// <summary>
    /// 注册指定程序集中所有标记了 <see cref="CacheEntityAttribute"/> 的类。
    /// 它们的加载器以 <see cref="IEntityLoader{TEntity}"/> 的形式从 DI 解析。
    /// </summary>
    /// <param name="assemblies">待扫描的程序集。</param>
    public void ScanAssemblies(params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        ScanAssembliesCore(assemblies);
    }

    /// <summary>
    /// 配置应用启动后是否自动预热全部已注册实体。
    /// </summary>
    /// <param name="enabled">是否启用启动预热，默认启用。</param>
    /// <returns>当前构建器实例。</returns>
    public EntityCacheBuilder WithWarmupOnStartup(bool enabled = true)
    {
        _serviceOptions.WarmupOnStartup = enabled;
        return this;
    }

    /// <summary>
    /// 配置启动预热的总超时时间。
    /// </summary>
    /// <param name="timeout">
    /// 预热超时；<see cref="TimeSpan.Zero"/> 表示不设超时。
    /// </param>
    /// <returns>当前构建器实例。</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="timeout"/> 为负数。
    /// </exception>
    public EntityCacheBuilder WithWarmupTimeout(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "must not be negative.");
        }

        _serviceOptions.WarmupTimeout = timeout;
        return this;
    }

    /// <summary>
    /// 配置启动预热失败时的行为。
    /// </summary>
    /// <param name="policy">启动失败策略。</param>
    /// <returns>当前构建器实例。</returns>
    public EntityCacheBuilder WithStartupFailurePolicy(StartupFailurePolicy policy)
    {
        _serviceOptions.StartupFailurePolicy = policy;
        return this;
    }

    /// <summary>
    /// 配置全量刷新时并行加载实体的最大数量。
    /// </summary>
    /// <param name="maxDegreeOfParallelism">并行度，必须大于等于 1。</param>
    /// <returns>当前构建器实例。</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxDegreeOfParallelism"/> 小于 1。
    /// </exception>
    public EntityCacheBuilder WithReloadAllParallelism(int maxDegreeOfParallelism)
    {
        if (maxDegreeOfParallelism < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxDegreeOfParallelism),
                maxDegreeOfParallelism,
                "must be at least 1.");
        }

        _serviceOptions.ReloadAllMaxDegreeOfParallelism = maxDegreeOfParallelism;
        return this;
    }

    /// <summary>
    /// 配置是否启用 <c>System.Diagnostics.Metrics</c> 指标。
    /// </summary>
    /// <param name="enabled">是否启用指标，默认启用。</param>
    /// <returns>当前构建器实例。</returns>
    public EntityCacheBuilder WithMetrics(bool enabled = true)
    {
        _serviceOptions.EnableMetrics = enabled;
        return this;
    }

    private void ScanAssembliesCore(IReadOnlyCollection<Assembly> assemblies)
    {
        foreach (var assembly in assemblies)
        {
            foreach (var type in GetScannableTypes(assembly))
            {
                var attribute = type.GetCustomAttribute<CacheEntityAttribute>(inherit: false);
                if (attribute is null || !type.IsClass || type.IsAbstract)
                {
                    continue;
                }

                AddRegistration(CreateScanRegistration(type, attribute));
            }
        }
    }

    private void AddRegistration(EntityCacheRegistration registration)
    {
        if (_registrations.Any(existing => existing.EntityType == registration.EntityType))
        {
            throw new InvalidOperationException(
                $"Entity type '{registration.EntityType.FullName}' is registered more than once.");
        }

        _registrations.Add(registration);
    }

    private static IEnumerable<Type> GetScannableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex) when (ex.Types is not null)
        {
            return ex.Types.Where(type => type is not null)!;
        }
    }

    private static EntityCacheRegistration CreateScanRegistration(
        Type entityType,
        CacheEntityAttribute attribute)
    {
        var method = typeof(EntityCacheBuilder)
            .GetMethod(nameof(CreateScanRegistrationCore), BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(EntityCacheBuilder), nameof(CreateScanRegistrationCore));

        try
        {
            return (EntityCacheRegistration)method
                .MakeGenericMethod(entityType)
                .Invoke(null, [attribute])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static EntityCacheRegistration<TEntity> CreateScanRegistrationCore<TEntity>(
        CacheEntityAttribute attribute)
        where TEntity : class
    {
        var entityBuilder = new EntityCacheEntityBuilder<TEntity>();
        entityBuilder.ApplyAttribute(attribute);
        return entityBuilder.Build();
    }
}
