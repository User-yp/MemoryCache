namespace MemoryCache.Abstractions;

/// <summary>
/// 访问（查询、刷新或失效）未注册到实体内存缓存的实体类型时抛出。
/// </summary>
/// <remarks>
/// 派生自 <see cref="InvalidOperationException"/>：既可以用
/// <see cref="EntityNotRegisteredException"/> 精确捕获，也可以用
/// <see cref="InvalidOperationException"/> 一并兜住。
/// </remarks>
public sealed class EntityNotRegisteredException : InvalidOperationException
{
    /// <summary>
    /// 使用未注册的实体类型初始化异常。
    /// </summary>
    /// <param name="entityType">未注册的实体类型。</param>
    public EntityNotRegisteredException(Type entityType)
        : base(CreateMessage(entityType))
        => EntityType = entityType;

    /// <summary>
    /// 获取未注册的实体类型。
    /// </summary>
    public Type EntityType { get; }

    private static string CreateMessage(Type entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);

        return $"Entity type '{entityType.FullName}' is not registered with the entity memory cache.";
    }
}
