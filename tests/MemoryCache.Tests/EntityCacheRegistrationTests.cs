using MemoryCache.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MemoryCache;

namespace MemoryCache.Tests;

public sealed class EntityCacheRegistrationTests
{
    [Fact]
    public void Delegate_loader_registration_exposes_a_singleton_service()
    {
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
        {
            builder.AddEntity<SampleSwitch>(entity => entity
                .WithKey(nameof(SampleSwitch.Code))
                .WithLoader(_ => new FakeSwitchLoader()));
        });

        using var provider = services.BuildServiceProvider();
        var cacheService = provider.GetRequiredService<IEntityCacheService>();

        Assert.Single(cacheService.RegisteredTypes);
        Assert.Contains(typeof(SampleSwitch), cacheService.RegisteredTypes);
        Assert.True(cacheService.IsRegistered<SampleSwitch>());
        Assert.False(cacheService.IsRegistered<SampleUnmarkedEntity>());
        Assert.Throws<InvalidOperationException>(() => cacheService.Get<SampleUnmarkedEntity>());

        var entry = cacheService.Get<SampleSwitch>();
        Assert.NotNull(entry);
        Assert.Equal(0, entry.Count);
        Assert.Equal(0, entry.Version);
        Assert.Empty(entry.GetSnapshot());

        Assert.Same(cacheService, provider.GetRequiredService<IEntityCacheService>());
    }

    [Fact]
    public void ScanAssembly_registration_resolves_loader_from_di()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEntityLoader<SampleSwitch>, FakeSwitchLoader>();
        services.AddSingleton<IEntityLoader<SampleComposite>, FakeCompositeLoader>();
        services.AddEntityMemoryCache(builder =>
            builder.ScanAssembly(typeof(SampleSwitch).Assembly));

        using var provider = services.BuildServiceProvider();
        var cacheService = provider.GetRequiredService<IEntityCacheService>();

        Assert.Equal(2, cacheService.RegisteredTypes.Count);
        Assert.Contains(typeof(SampleSwitch), cacheService.RegisteredTypes);
        Assert.Contains(typeof(SampleComposite), cacheService.RegisteredTypes);
        Assert.True(cacheService.IsRegistered<SampleSwitch>());

        var entry = cacheService.Get<SampleSwitch>();
        Assert.NotNull(entry);
        Assert.Equal(0, entry.Version);
    }

    [Fact]
    public void ScanAssembly_registers_only_marked_entity_types()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEntityLoader<SampleSwitch>, FakeSwitchLoader>();
        services.AddSingleton<IEntityLoader<SampleComposite>, FakeCompositeLoader>();
        services.AddEntityMemoryCache(builder =>
            builder.ScanAssembly(typeof(SampleUnmarkedEntity).Assembly));

        using var provider = services.BuildServiceProvider();
        var cacheService = provider.GetRequiredService<IEntityCacheService>();

        Assert.Equal(2, cacheService.RegisteredTypes.Count);
        Assert.Contains(typeof(SampleSwitch), cacheService.RegisteredTypes);
        Assert.Contains(typeof(SampleComposite), cacheService.RegisteredTypes);
        Assert.DoesNotContain(typeof(SampleUnmarkedEntity), cacheService.RegisteredTypes);
    }

    [Fact]
    public void ScanAssembly_fails_fast_when_di_loader_is_missing()
    {
        var services = new ServiceCollection();
        services.AddEntityMemoryCache(builder =>
            builder.ScanAssembly(typeof(SampleSwitch).Assembly));

        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IEntityCacheService>());
        Assert.Contains(nameof(SampleSwitch), exception.Message);
    }

    [Fact]
    public void Explicit_registration_without_loader_uses_di_loader()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEntityLoader<SampleSwitch>, FakeSwitchLoader>();
        services.AddEntityMemoryCache(builder =>
            builder.AddEntity<SampleSwitch>(entity => entity.WithKey(item => item.Code)));

        using var provider = services.BuildServiceProvider();
        var cacheService = provider.GetRequiredService<IEntityCacheService>();

        Assert.True(cacheService.IsRegistered<SampleSwitch>());
        Assert.NotNull(cacheService.Get<SampleSwitch>());
    }

    [Fact]
    public void Registering_the_same_entity_twice_throws()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddEntityMemoryCache(builder =>
            {
                builder.AddEntity<SampleSwitch>();
                builder.AddEntity<SampleSwitch>();
            }));

        Assert.Contains("more than once", exception.Message);
    }

    [Fact]
    public void Registering_the_service_twice_throws()
    {
        var services = new ServiceCollection();
        services.AddEntityMemoryCache();

        Assert.Throws<InvalidOperationException>(() => services.AddEntityMemoryCache());
    }

    [Fact]
    public void Invalid_key_property_name_fails_at_registration_time()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() =>
            services.AddEntityMemoryCache(builder =>
                builder.AddEntity<SampleSwitch>(entity =>
                    entity.WithKey("NoSuchProperty"))));
    }

    [Fact]
    public void Empty_registration_still_exposes_the_service()
    {
        var services = new ServiceCollection();
        services.AddEntityMemoryCache();

        using var provider = services.BuildServiceProvider();
        var cacheService = provider.GetRequiredService<IEntityCacheService>();

        Assert.Empty(cacheService.RegisteredTypes);
        Assert.Throws<InvalidOperationException>(() => cacheService.Get<SampleSwitch>());
    }
}
