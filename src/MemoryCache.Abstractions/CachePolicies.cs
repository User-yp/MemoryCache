namespace MemoryCache.Abstractions;

/// <summary>
/// 定义对某个缓存项调用 <see cref="IEntityCacheService.Invalidate{TEntity}"/>
/// 时的处理行为。
/// </summary>
public enum InvalidationMode
{
    /// <summary>
    /// 将缓存项标记为失效，并立即在后台触发一次刷新。
    /// </summary>
    ReloadInBackground = 0,

    /// <summary>
    /// 仅将缓存项标记为失效；刷新延迟到调用 <c>EnsureFreshAsync</c>
    /// 或显式请求刷新时再进行。
    /// </summary>
    MarkStaleOnly = 1,
}

/// <summary>
/// 定义加载过程中发现重复键时的处理方式。
/// </summary>
public enum DuplicateKeyPolicy
{
    /// <summary>
    /// 记录警告并保留后加载的条目。
    /// </summary>
    LogWarningAndKeepLast = 0,

    /// <summary>
    /// 记录警告并保留先加载的条目。
    /// </summary>
    KeepFirst = 1,

    /// <summary>
    /// 将整次刷新视为失败；继续使用上一次的快照。
    /// </summary>
    Throw = 2,
}

/// <summary>
/// 定义启动预热失败时组件的行为。
/// </summary>
public enum StartupFailurePolicy
{
    /// <summary>
    /// 记录失败并允许应用继续启动；后续的刷新尝试会自动重试。
    /// </summary>
    Continue = 0,

    /// <summary>
    /// 向上抛出预热失败，使应用启动快速失败。
    /// </summary>
    Throw = 1,
}
