using MemoryCache.Abstractions;

namespace MemoryCache.Sample.Redis;

/// <summary>
/// 开关表的实体镜像（示例用内存假库代替真实数据库）。
/// </summary>
[CacheEntity(KeyProperty = nameof(Code))]
public sealed class FeatureSwitch
{
    public string Code { get; set; } = "";

    public string Value { get; set; } = "";

    public bool Enabled { get; set; }
}
