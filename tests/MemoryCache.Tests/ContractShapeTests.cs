using System.Reflection;
using MemoryCache.Abstractions;
using Xunit;

namespace MemoryCache.Tests;

public sealed class ContractShapeTests
{
    private const BindingFlags PublicInstance =
        BindingFlags.Public | BindingFlags.Instance;

    [Fact]
    public void EntityCache_exposes_the_documented_contract()
    {
        var cacheType = typeof(IEntityCache<SampleSwitch>);

        Assert.NotNull(cacheType.GetProperty(nameof(IEntityCache<SampleSwitch>.Version), PublicInstance));
        Assert.NotNull(cacheType.GetProperty(nameof(IEntityCache<SampleSwitch>.Count), PublicInstance));
        Assert.NotNull(cacheType.GetProperty(nameof(IEntityCache<SampleSwitch>.IsStale), PublicInstance));
        Assert.NotNull(cacheType.GetProperty(nameof(IEntityCache<SampleSwitch>.LoadedAt), PublicInstance));
        Assert.NotNull(cacheType.GetProperty(nameof(IEntityCache<SampleSwitch>.LastError), PublicInstance));
        Assert.NotNull(cacheType.GetMethod(nameof(IEntityCache<SampleSwitch>.GetSnapshot), PublicInstance));
        Assert.NotNull(cacheType.GetMethod(nameof(IEntityCache<SampleSwitch>.AsQueryable), PublicInstance));
        Assert.NotNull(cacheType.GetMethod(nameof(IEntityCache<SampleSwitch>.ContainsKey), PublicInstance));
        Assert.NotNull(cacheType.GetMethod(nameof(IEntityCache<SampleSwitch>.TryGetValue), PublicInstance));
        Assert.NotNull(cacheType.GetMethod(nameof(IEntityCache<SampleSwitch>.GetByKey), PublicInstance));
        Assert.NotNull(cacheType.GetMethod(nameof(IEntityCache<SampleSwitch>.ReloadAsync), PublicInstance));
        Assert.NotNull(cacheType.GetMethod(nameof(IEntityCache<SampleSwitch>.EnsureFreshAsync), PublicInstance));
        Assert.NotNull(cacheType.GetEvent(nameof(IEntityCache<SampleSwitch>.Reloaded), PublicInstance));
    }

    [Fact]
    public void CacheService_exposes_the_documented_contract()
    {
        var serviceType = typeof(IEntityCacheService);
        var reloadMethods = serviceType
            .GetMethods(PublicInstance)
            .Where(method => method.Name == nameof(IEntityCacheService.ReloadAsync))
            .ToList();
        var invalidateMethods = serviceType
            .GetMethods(PublicInstance)
            .Where(method => method.Name == nameof(IEntityCacheService.Invalidate))
            .ToList();

        Assert.NotNull(serviceType.GetProperty(nameof(IEntityCacheService.RegisteredTypes), PublicInstance));
        Assert.NotNull(serviceType.GetMethod(nameof(IEntityCacheService.Get), PublicInstance));
        Assert.NotNull(serviceType.GetMethod(nameof(IEntityCacheService.IsRegistered), PublicInstance));
        Assert.NotNull(serviceType.GetMethod(nameof(IEntityCacheService.ReloadAllAsync), PublicInstance));
        Assert.NotNull(serviceType.GetEvent(nameof(IEntityCacheService.Invalidated), PublicInstance));
        Assert.Equal(2, reloadMethods.Count);
        Assert.Contains(reloadMethods, method =>
            method.IsGenericMethodDefinition
            && method.GetGenericArguments().Length == 1
            && method.GetParameters().Length == 1);
        Assert.Contains(reloadMethods, method =>
            !method.IsGenericMethodDefinition
            && method.GetParameters().Length == 2
            && method.GetParameters()[0].ParameterType == typeof(Type));
        Assert.Equal(2, invalidateMethods.Count);
        Assert.Contains(invalidateMethods, method =>
            method.IsGenericMethodDefinition
            && method.GetGenericArguments().Length == 1);
        Assert.Contains(invalidateMethods, method =>
            !method.IsGenericMethodDefinition
            && method.GetParameters().Length == 1
            && method.GetParameters()[0].ParameterType == typeof(Type));
    }

    [Fact]
    public void Loader_exposes_the_documented_contract()
    {
        var loaderType = typeof(IEntityLoader<SampleSwitch>);

        var load = loaderType.GetMethod(nameof(IEntityLoader<SampleSwitch>.LoadAsync), PublicInstance);
        Assert.NotNull(load);
        Assert.Single(load.GetParameters());
        Assert.Equal(typeof(CancellationToken), load.GetParameters()[0].ParameterType);
        Assert.True(typeof(Task).IsAssignableFrom(load.ReturnType));
    }

    [Fact]
    public void Entity_interface_requires_a_reference_type()
    {
        var genericArgument = typeof(IEntityCache<>).GetGenericArguments().Single();

        Assert.True(genericArgument.GenericParameterAttributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint));
    }

    // 该方法不会被测试真正执行：它仅作为编译期契约检查，保证公共 API 可被
    // 使用方代码正常调用；一旦签名变化，构建就会失败。
    private static void CompileTimeContractSmoke(
        IEntityCache<SampleSwitch> cache,
        IEntityCacheService service,
        IEntityLoader<SampleSwitch> loader)
    {
        _ = cache.Version;
        _ = cache.Count;
        _ = cache.IsStale;
        _ = cache.LoadedAt;
        _ = cache.LastError;
        _ = cache.GetSnapshot();
        _ = cache.AsQueryable();
        _ = cache.ContainsKey("key");
        _ = cache.TryGetValue("key", out var _);
        _ = cache.GetByKey("key");
        _ = cache.ReloadAsync();
        _ = cache.EnsureFreshAsync(CancellationToken.None);
        cache.Reloaded += (_, _) => { };

        _ = service.Get<SampleSwitch>();
        _ = service.IsRegistered<SampleSwitch>();
        _ = service.RegisteredTypes;
        _ = service.ReloadAsync<SampleSwitch>();
        _ = service.ReloadAllAsync();
        service.Invalidate<SampleSwitch>();
        service.Invalidated += (_, _) => { };

        _ = loader.LoadAsync(CancellationToken.None);
    }
}
