using MemoryCache.Abstractions;

namespace MemoryCache.Redis;

/// <summary>
/// 为所有参与 Redis 共享快照的实体自动包裹加载器。
/// </summary>
/// <typeparam name="TEntity">实体类型。</typeparam>
internal sealed class RedisEntityLoaderDecorator<TEntity> : IEntityLoaderDecorator<TEntity>
    where TEntity : class
{
    private readonly IRedisEntitySnapshotStore<TEntity> _store;
    private readonly RedisEntityCacheOptions _options;

    public RedisEntityLoaderDecorator(
        IRedisEntitySnapshotStore<TEntity> store,
        RedisEntityCacheOptions options)
    {
        _store = store;
        _options = options;
    }

    public IEntityLoader<TEntity> Decorate(IEntityLoader<TEntity> loader)
    {
        ArgumentNullException.ThrowIfNull(loader);

        return _options.ShouldUseRedis(typeof(TEntity))
            ? new RedisBackedEntityLoader<TEntity>(loader, _store, _options)
            : loader;
    }
}
