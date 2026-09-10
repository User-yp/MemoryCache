namespace MemoryCache.Abstractions;

/// <summary>
/// 从数据源加载某个实体类型的全量数据。
/// </summary>
/// <typeparam name="TEntity">被缓存的实体类型。</typeparam>
/// <remarks>
/// 实现与 ORM 无关：可以基于 EF Core、FreeSql、Dapper 或任意数据访问技术。
/// 缓存每次刷新都会在独立 DI 作用域中解析加载器，因此即使缓存服务本身是单例，
/// 也支持作用域（Scoped）或瞬时（Transient）加载器。
/// </remarks>
public interface IEntityLoader<TEntity>
    where TEntity : class
{
    /// <summary>
    /// 加载 <typeparamref name="TEntity"/> 的完整条目集合。
    /// </summary>
    /// <param name="cancellationToken">用于取消加载操作的令牌。</param>
    /// <returns>需要写入缓存快照的完整条目集合。</returns>
    Task<IReadOnlyCollection<TEntity>> LoadAsync(CancellationToken cancellationToken);
}
