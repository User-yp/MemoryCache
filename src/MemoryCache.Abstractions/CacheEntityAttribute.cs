namespace MemoryCache.Abstractions;

/// <summary>
/// 将某个类标记为可缓存实体：它的数据会从数据库全量加载到进程内实体缓存中。
/// </summary>
/// <remarks>
/// 该特性只负责声明元数据（键约定、刷新周期与安全阈值），本身不会加载数据；
/// 实际的取数逻辑由随实体一起注册的 <see cref="IEntityLoader{TEntity}"/> 提供。
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class CacheEntityAttribute : Attribute
{
    /// <summary>
    /// 获取或设置用作缓存键的属性名。
    /// </summary>
    /// <remarks>
    /// 默认支持单个属性；复合键可使用逗号分隔的属性名列表表示，
    /// 例如 <c>"TenantId,Code"</c>。当值为 <c>null</c> 时，
    /// 按惯例查找名为 <c>Id</c> 的属性。
    /// </remarks>
    public string? KeyProperty { get; set; }

    /// <summary>
    /// 获取或设置该缓存项的显式注册名/日志标签。
    /// </summary>
    /// <remarks>
    /// 当值为 <c>null</c> 时使用实体类型名。
    /// </remarks>
    public string? Name { get; set; }

    /// <summary>
    /// 获取或设置条目数超过后触发告警日志的阈值。
    /// </summary>
    /// <remarks>
    /// 缓存面向小型的开关/配置表；该阈值是安全护栏而非淘汰策略。默认值为 50,000。
    /// </remarks>
    public long CapacityWarningThreshold { get; set; } = 50_000;

    /// <summary>
    /// 获取或设置定时刷新周期（秒）。
    /// </summary>
    /// <remarks>
    /// 值为 <c>0</c>（默认）表示不启用定时刷新。
    /// </remarks>
    public double RefreshIntervalSeconds { get; set; }
}
