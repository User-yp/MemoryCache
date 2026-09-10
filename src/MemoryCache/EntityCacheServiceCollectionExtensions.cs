using MemoryCache.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace MemoryCache;

/// <summary>
/// 用于注册实体内存缓存服务的 DI 扩展方法。
/// </summary>
public static class EntityCacheServiceCollectionExtensions
{
    /// <summary>
    /// 添加实体内存缓存服务，并应用 <paramref name="configure"/> 描述的注册配置。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configure">可选的缓存注册配置。</param>
    /// <returns>返回同一个服务集合，便于链式调用。</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="services"/> 为 <c>null</c>。
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// 实体内存缓存服务已注册过，或注册配置无效。
    /// </exception>
    public static IServiceCollection AddEntityMemoryCache(
        this IServiceCollection services,
        Action<EntityCacheBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(IEntityCacheService)))
        {
            throw new InvalidOperationException(
                "The entity memory cache service has already been registered.");
        }

        var builder = new EntityCacheBuilder();
        configure?.Invoke(builder);

        var options = new EntityCacheOptions
        {
            Registrations = builder.Registrations.ToArray(),
            ServiceOptions = builder.ServiceOptions,
        };

        services.AddSingleton(options);
        // 指标由容器持有：同一个 ServiceCollection 构建多个容器时各自独立，
        // 也不会把同一个 Meter 的仪表注册两遍。
        services.AddSingleton(_ => new CacheMetrics(builder.ServiceOptions));

        // 同一个实例同时作为缓存服务与“原始取数”入口对外暴露。
        services.AddSingleton(serviceProvider => new EntityCacheService(serviceProvider, options));
        services.AddSingleton<IEntityCacheService>(serviceProvider =>
            serviceProvider.GetRequiredService<EntityCacheService>());
        services.AddSingleton<IEntitySourceLoader>(serviceProvider =>
            serviceProvider.GetRequiredService<EntityCacheService>());

        if (options.ServiceOptions.WarmupOnStartup)
        {
            services.AddHostedService<EntityCacheWarmupHostedService>();
        }

        if (options.Registrations.Any(registration => registration.RefreshIntervalSeconds > 0))
        {
            services.AddHostedService<EntityCachePeriodicRefresher>();
        }

        return services;
    }
}
