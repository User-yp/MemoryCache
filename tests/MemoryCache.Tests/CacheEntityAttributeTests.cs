using System.Reflection;
using MemoryCache.Abstractions;
using Xunit;

namespace MemoryCache.Tests;

public sealed class CacheEntityAttributeTests
{
    [Fact]
    public void Attribute_targets_classes_only_once_and_not_inherited()
    {
        var usage = typeof(CacheEntityAttribute).GetCustomAttribute<AttributeUsageAttribute>();

        Assert.NotNull(usage);
        Assert.True(usage.ValidOn.HasFlag(AttributeTargets.Class));
        Assert.False(usage.ValidOn.HasFlag(AttributeTargets.Property));
        Assert.False(usage.ValidOn.HasFlag(AttributeTargets.Method));
        Assert.False(usage.AllowMultiple);
        Assert.False(usage.Inherited);
    }

    [Fact]
    public void Attribute_type_is_sealed()
    {
        Assert.True(typeof(CacheEntityAttribute).IsSealed);
    }

    [Fact]
    public void Attribute_has_the_documented_defaults()
    {
        var attribute = new CacheEntityAttribute();

        Assert.Null(attribute.KeyProperty);
        Assert.Null(attribute.Name);
        Assert.Equal(50_000, attribute.CapacityWarningThreshold);
        Assert.Equal(0, attribute.RefreshIntervalSeconds);
    }

    [Fact]
    public void Marked_entity_exposes_configured_metadata()
    {
        var attribute = typeof(SampleSwitch).GetCustomAttribute<CacheEntityAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(nameof(SampleSwitch.Code), attribute.KeyProperty);
        Assert.Equal(50_000, attribute.CapacityWarningThreshold);
        Assert.Equal(0, attribute.RefreshIntervalSeconds);
    }

    [Fact]
    public void Unmarked_entity_has_no_cache_attribute()
    {
        var attribute = typeof(SampleUnmarkedEntity).GetCustomAttribute<CacheEntityAttribute>();

        Assert.Null(attribute);
    }
}
