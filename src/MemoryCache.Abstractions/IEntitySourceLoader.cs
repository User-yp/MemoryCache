namespace MemoryCache.Abstractions;

/// <summary>
/// 绕开加载器装饰、直接调用使用方配置的原始加载器取数。
/// </summary>
/// <remarks>
/// <para>
/// 这是给扩展组件用的入口：例如 Redis 共享快照的写入方在回填共享存储时，需要拿到数据库的
/// 最新数据，而不能被共享快照（L2）挡住。普通业务代码应使用
/// <see cref="IEntityCacheService.ReloadAsync{TEntity}(CancellationToken)"/>。
/// </para>
/// <para>
/// 该操作<b>不会</b>更新进程内快照，也不会触发 <c>Reloaded</c> 事件。
/// </para>
/// </remarks>
public interface IEntitySourceLoader
{
    /// <summary>
    /// 用原始加载器加载指定实体的全量数据。
    /// </summary>
    /// <typeparam name="TEntity">实体类型。</typeparam>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>加载器返回的全部条目。</returns>
    /// <exception cref="KeyNotFoundException">实体未注册。</exception>
    Task<IReadOnlyList<TEntity>> LoadAsync<TEntity>(CancellationToken cancellationToken = default)
        where TEntity : class;
}
