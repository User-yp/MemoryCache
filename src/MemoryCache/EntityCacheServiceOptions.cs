using MemoryCache.Abstractions;

namespace MemoryCache;

/// <summary>
/// 实体缓存服务的全局选项。
/// </summary>
public sealed class EntityCacheServiceOptions
{
    /// <summary>
    /// 获取或设置是否在应用启动后自动预热全部已注册实体。
    /// </summary>
    /// <remarks>默认值为 <c>true</c>。</remarks>
    public bool WarmupOnStartup { get; set; } = true;

    /// <summary>
    /// 获取或设置启动预热的总超时时间。
    /// </summary>
    /// <remarks>
    /// 默认值为 30 秒；设置为 <see cref="TimeSpan.Zero"/> 表示不设超时。
    /// </remarks>
    public TimeSpan WarmupTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 获取或设置启动预热失败时的行为。
    /// </summary>
    /// <remarks>默认值为 <see cref="StartupFailurePolicy.Continue"/>。</remarks>
    public StartupFailurePolicy StartupFailurePolicy { get; set; } =
        StartupFailurePolicy.Continue;

    /// <summary>
    /// 获取或设置全量刷新时并行执行加载的最大实体数量。
    /// </summary>
    /// <remarks>
    /// 默认值为 <c>min(4, CPU 核心数)</c>；设为 <c>1</c> 表示严格串行。
    /// 定时刷新在同一轮内同时到期的多个实体时也遵循该上限。
    /// </remarks>
    public int ReloadAllMaxDegreeOfParallelism { get; set; } =
        Math.Max(1, Math.Min(4, Environment.ProcessorCount));

    /// <summary>
    /// 获取或设置是否启用基于 <c>System.Diagnostics.Metrics</c> 的指标。
    /// </summary>
    /// <remarks>默认值为 <c>true</c>；没有监听器时指标几乎是零开销的。</remarks>
    public bool EnableMetrics { get; set; } = true;
}
