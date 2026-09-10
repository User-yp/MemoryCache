using MemoryCache.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace MemoryCache.Redis;

/// <summary>
/// Redis 共享快照的注册扩展。
/// </summary>
public static class RedisEntityCacheServiceCollectionExtensions
{
    /// <summary>
    /// 为实体内存缓存启用 Redis 共享快照（两级缓存）。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configure">可选的配置回调。</param>
    /// <returns>返回同一个服务集合，便于链式调用。</returns>
    /// <exception cref="InvalidOperationException">
    /// Redis 共享快照已注册过。
    /// </exception>
    /// <remarks>
    /// 需要先调用 <c>AddEntityMemoryCache(...)</c>。启用后：进程内快照是 L1，
    /// Redis 是 L2；L1 失效后的刷新先读 Redis，Redis 缺失或过期时回源数据库并回填。
    /// </remarks>
    public static IServiceCollection AddEntityMemoryCacheRedis(
        this IServiceCollection services,
        Action<RedisEntityCacheOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(RedisEntityCacheOptions)))
        {
            throw new InvalidOperationException(
                "Redis support for the entity memory cache has already been registered.");
        }

        var options = new RedisEntityCacheOptions();
        configure?.Invoke(options);
        options.Validate();

        services.AddSingleton(options);
        services.AddSingleton<IConnectionMultiplexer>(_ => CreateConnection(options));
        services.AddSingleton(
            typeof(IRedisEntitySnapshotStore<>),
            typeof(RedisEntitySnapshotStore<>));
        services.AddSingleton(
            typeof(IEntityLoaderDecorator<>),
            typeof(RedisEntityLoaderDecorator<>));
        services.AddSingleton<IRedisEntityInvalidationChannel, RedisEntityInvalidationChannel>();
        services.AddSingleton<IRedisEntitySnapshotMaintenance, RedisEntitySnapshotMaintenance>();
        services.AddHostedService<RedisEntityInvalidationSubscriber>();

        return services;
    }

    private static IConnectionMultiplexer CreateConnection(RedisEntityCacheOptions options)
    {
        var configuration = ConfigurationOptions.Parse(options.Configuration);

        // 连不上时不要在启动阶段抛异常：组件会降级为“只用进程内缓存 + 直接回源数据库”。
        configuration.AbortOnConnectFail = false;
        configuration.ConnectTimeout = (int)Math.Min(
            options.ConnectTimeout.TotalMilliseconds,
            int.MaxValue);

        return ConnectionMultiplexer.Connect(configuration);
    }
}
