using MemoryCache.Abstractions;

namespace MemoryCache.Sample.EFCore;

/// <summary>
/// <c>job_config</c> 表的实体（复合主键：GROUP + JOB_KEYNAME）。
/// </summary>
[CacheEntity(KeyProperty = "Group,JobKeyName")]
public sealed class JobConfig
{
    public string Group { get; set; } = "";

    public string JobKeyName { get; set; } = "";

    public string? JobDesc { get; set; }

    public string TriggerKeyName { get; set; } = "";

    public string? TriggerDesc { get; set; }

    public string Cron { get; set; } = "";

    public string? CronDesc { get; set; }

    /// <summary>IS_ENABLE 为 'Y' 时返回 true。</summary>
    public bool IsEnabled { get; set; }
}
