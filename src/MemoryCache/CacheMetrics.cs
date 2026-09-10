using System.Diagnostics.Metrics;

namespace MemoryCache;

/// <summary>
/// 实体内存缓存的指标采集器。
/// </summary>
internal sealed class CacheMetrics : IDisposable
{
    private const string MeterName = "MemoryCache";

    private readonly Meter? _meter;
    private readonly Counter<long>? _reloadsTotal;
    private readonly Histogram<double>? _reloadDuration;

    public CacheMetrics(EntityCacheServiceOptions options)
    {
        if (!options.EnableMetrics)
        {
            return;
        }

        _meter = new Meter(MeterName, "0.1.0");
        _reloadsTotal = _meter.CreateCounter<long>(
            "memorycache.reloads_total",
            "count",
            "实体缓存刷新次数（含成功与失败）");
        _reloadDuration = _meter.CreateHistogram<double>(
            "memorycache.reload_duration_seconds",
            "seconds",
            "实体缓存刷新耗时");
    }

    /// <summary>
    /// 注册快照条目数与已注册类型数两个可观测指标。
    /// </summary>
    /// <param name="registeredTypeCountProvider">已注册实体数的取值函数。</param>
    /// <param name="snapshotItemsProvider">各实体快照条目数的测量序列。</param>
    public void ConfigureGaugeProviders(
        Func<int> registeredTypeCountProvider,
        Func<IEnumerable<Measurement<int>>> snapshotItemsProvider)
    {
        if (_meter is null)
        {
            return;
        }

        _meter.CreateObservableGauge<int>(
            "memorycache.registered_types",
            () => registeredTypeCountProvider());
        _meter.CreateObservableGauge<int>(
            "memorycache.snapshot_items",
            snapshotItemsProvider);
    }

    /// <summary>
    /// 记录一次刷新结果。
    /// </summary>
    /// <param name="entityName">实体注册名。</param>
    /// <param name="success">刷新是否成功。</param>
    /// <param name="durationSeconds">刷新耗时（秒）。</param>
    public void RecordReload(string entityName, bool success, double durationSeconds)
    {
        if (_reloadsTotal is null)
        {
            return;
        }

        var reloadTags = new[]
        {
            new KeyValuePair<string, object?>("entity", entityName),
            new KeyValuePair<string, object?>("outcome", success ? "success" : "failure"),
        };
        _reloadsTotal.Add(1, reloadTags);

        _reloadDuration?.Record(
            durationSeconds,
            new KeyValuePair<string, object?>("entity", entityName));
    }

    public void Dispose()
        => _meter?.Dispose();
}
