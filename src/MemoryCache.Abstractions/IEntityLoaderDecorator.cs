namespace MemoryCache.Abstractions;

/// <summary>
/// 装饰实体加载器：在不修改原加载器的前提下插入额外行为
/// （例如先读共享缓存，未命中再回源数据库）。
/// </summary>
/// <typeparam name="TEntity">被缓存的实体类型。</typeparam>
/// <remarks>
/// 每次刷新都会在独立的 DI 作用域内解析该实体类型的全部装饰器，并按注册顺序
/// 由内到外逐层包装加载器；没有注册任何装饰器时行为与直接使用加载器一致。
/// </remarks>
public interface IEntityLoaderDecorator<TEntity>
    where TEntity : class
{
    /// <summary>
    /// 包装加载器并返回新的加载器。
    /// </summary>
    /// <param name="loader">已解析的原始加载器。</param>
    /// <returns>包装后的加载器；不允许返回 <c>null</c>。</returns>
    IEntityLoader<TEntity> Decorate(IEntityLoader<TEntity> loader);
}
