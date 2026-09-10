using MemoryCache.Abstractions;

namespace MemoryCache.Tests;

[CacheEntity(KeyProperty = nameof(SampleSwitch.Code))]
public sealed class SampleSwitch
{
    public long Id { get; set; }

    public string Code { get; set; } = "";

    public string Value { get; set; } = "";

    public bool Enabled { get; set; }
}

[CacheEntity(KeyProperty = "Group,Code")]
public sealed class SampleComposite
{
    public string Group { get; set; } = "";

    public string Code { get; set; } = "";

    public string Value { get; set; } = "";
}

public sealed class SampleUnmarkedEntity
{
    public long Id { get; set; }
}

public sealed class SampleKeylessEntity
{
    public string Value { get; set; } = "";
}

/// <summary>用于验证 [CacheEntity] 上的 LoadTimeoutSeconds 会被装配成刷新超时。</summary>
[CacheEntity(LoadTimeoutSeconds = 0.1)]
public sealed class SampleTimeoutEntity
{
    public long Id { get; set; }

    public string Value { get; set; } = "";
}

/// <summary>
/// 用于验证 [CacheEntity] 上声明的数据策略元数据会被装配生效：
/// 这里刻意声明与默认值相反的取值（默认是保留最后一条 / 不索引但保留条目）。
/// </summary>
[CacheEntity(
    KeyProperty = nameof(Code),
    DuplicateKeyPolicy = DuplicateKeyPolicy.KeepFirst,
    NullKeyPolicy = NullKeyPolicy.Throw)]
public sealed class SamplePolicyEntity
{
    public string Code { get; set; } = "";

    public string Value { get; set; } = "";
}
