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
