using MemoryCache.Abstractions;

namespace MemoryCache.Sample;

/// <summary>
/// <c>job_config</c> 表的实体镜像（复合主键：GROUP + JOB_KEYNAME）。
/// </summary>
[CacheEntity(KeyProperty = "Group,JobKeyName")]
public sealed class JobConfig
{
    public string Group { get; init; } = "";

    public string JobKeyName { get; init; } = "";

    public string? JobDesc { get; init; }

    public string TriggerKeyName { get; init; } = "";

    public string? TriggerDesc { get; init; }

    public string Cron { get; init; } = "";

    public string? CronDesc { get; init; }

    /// <summary>IS_ENABLE 为 'Y' 时返回 true。</summary>
    public bool IsEnabled { get; init; }
}
