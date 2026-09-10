namespace MemoryCache.Sample.Redis;

/// <summary>
/// 进程内的“数据库”假实现：示例用它替代真实库，便于在一台机器上跑通全链路。
/// </summary>
/// <remarks>真实场景中它就是你自己的 DbContext / SqlConnection。</remarks>
public sealed class FakeSwitchDatabase
{
    private readonly object _gate = new();
    private readonly Dictionary<string, FeatureSwitch> _rows = new(StringComparer.Ordinal);
    private int _queryCount;

    /// <summary>获取全表查询次数，用于观察缓存是否真的挡掉了数据库访问。</summary>
    public int QueryCount => Volatile.Read(ref _queryCount);

    public void Upsert(FeatureSwitch row)
    {
        lock (_gate)
        {
            _rows[row.Code] = row;
        }
    }

    public IReadOnlyCollection<FeatureSwitch> QueryAll()
    {
        Interlocked.Increment(ref _queryCount);

        lock (_gate)
        {
            return _rows.Values
                .Select(row => new FeatureSwitch
                {
                    Code = row.Code,
                    Value = row.Value,
                    Enabled = row.Enabled,
                })
                .ToList();
        }
    }
}
