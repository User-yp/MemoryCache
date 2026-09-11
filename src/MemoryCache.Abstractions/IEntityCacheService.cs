namespace MemoryCache.Abstractions;

/// <summary>
/// 所有已注册实体缓存项的单例注册表与统一入口。
/// </summary>
public interface IEntityCacheService
{
    /// <summary>
    /// 返回为 <typeparamref name="TEntity"/> 注册的缓存项。
    /// </summary>
    /// <typeparam name="TEntity">被缓存的实体类型。</typeparam>
    /// <exception cref="EntityNotRegisteredException">
    /// 当 <typeparamref name="TEntity"/> 尚未注册时抛出。
    /// </exception>
    IEntityCache<TEntity> Get<TEntity>()
        where TEntity : class;

    /// <summary>
    /// 已为 <typeparamref name="TEntity"/> 注册缓存项时返回 <c>true</c>。
    /// </summary>
    /// <typeparam name="TEntity">被缓存的实体类型。</typeparam>
    bool IsRegistered<TEntity>()
        where TEntity : class;

    /// <summary>
    /// 获取当前已注册缓存项的实体类型集合。
    /// </summary>
    IReadOnlyCollection<Type> RegisteredTypes { get; }

    /// <summary>
    /// 执行部分刷新：仅刷新 <typeparamref name="TEntity"/> 对应的缓存项。
    /// </summary>
    /// <typeparam name="TEntity">被缓存的实体类型。</typeparam>
    /// <param name="cancellationToken">用于取消刷新的令牌。</param>
    Task ReloadAsync<TEntity>(CancellationToken cancellationToken = default)
        where TEntity : class;

    /// <summary>
    /// 针对给定的运行时实体类型执行部分刷新。
    /// </summary>
    /// <param name="entityType">被缓存的实体类型。</param>
    /// <param name="cancellationToken">用于取消刷新的令牌。</param>
    /// <exception cref="EntityNotRegisteredException">
    /// 未为 <paramref name="entityType"/> 注册缓存项时抛出。
    /// </exception>
    Task ReloadAsync(Type entityType, CancellationToken cancellationToken = default);

    /// <summary>
    /// 执行全量刷新：并行刷新所有已注册的实体类型。
    /// </summary>
    /// <param name="cancellationToken">用于取消刷新的令牌。</param>
    Task ReloadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 通知缓存：<typeparamref name="TEntity"/> 的数据已发生变化。
    /// 具体行为由该缓存项的失效模式决定。
    /// </summary>
    /// <typeparam name="TEntity">被缓存的实体类型。</typeparam>
    void Invalidate<TEntity>()
        where TEntity : class;

    /// <summary>
    /// 通知缓存：<paramref name="entityType"/> 的数据已发生变化。
    /// 具体行为由该缓存项的失效模式决定。
    /// </summary>
    /// <param name="entityType">被缓存的实体类型。</param>
    /// <exception cref="EntityNotRegisteredException">
    /// 未为 <paramref name="entityType"/> 注册缓存项时抛出。
    /// </exception>
    void Invalidate(Type entityType);

    /// <summary>
    /// 某个实体缓存项被失效时触发，发生在按失效模式执行刷新行为之前。
    /// </summary>
    event EventHandler<EntityCacheInvalidatedEventArgs>? Invalidated;
}
