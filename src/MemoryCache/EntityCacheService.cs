using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using MemoryCache.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace MemoryCache;

/// <summary>
/// <see cref="IEntityCacheService"/> 的默认单例实现。
/// </summary>
internal sealed class EntityCacheService : IEntityCacheService, IDisposable
{
    private readonly ConcurrentDictionary<Type, IEntityCache> _entries = new();

    private readonly IReadOnlyDictionary<Type, EntityCacheRegistration> _registrations;

    private readonly EntityCacheServiceOptions _serviceOptions;

    private readonly CacheMetrics _metrics;

    private readonly CancellationTokenSource _lifetimeSource = new();

    public EntityCacheService(IServiceProvider serviceProvider, EntityCacheOptions options)
    {
        ValidateDiLoaders(serviceProvider, options);

        _serviceOptions = options.ServiceOptions;
        _metrics = serviceProvider.GetRequiredService<CacheMetrics>();
        _registrations = options.Registrations.ToDictionary(
            registration => registration.EntityType,
            registration => registration);

        foreach (var registration in options.Registrations)
        {
            _entries.TryAdd(
                registration.EntityType,
                registration.CreateEntry(serviceProvider, _lifetimeSource.Token));
        }

        _metrics.ConfigureGaugeProviders(
            () => _entries.Count,
            () => _entries.Select(entry => new Measurement<int>(
                entry.Value.Count,
                new KeyValuePair<string, object?>(
                    "entity",
                    _registrations[entry.Key].Name))));
    }

    public IReadOnlyCollection<Type> RegisteredTypes
        => _entries.Keys.ToArray();

    public event EventHandler<EntityCacheInvalidatedEventArgs>? Invalidated;

    public IEntityCache<TEntity> Get<TEntity>()
        where TEntity : class
    {
        if (_entries.TryGetValue(typeof(TEntity), out var cache) && cache is IEntityCache<TEntity> typedCache)
        {
            return typedCache;
        }

        throw new InvalidOperationException(
            $"Entity type '{typeof(TEntity).FullName}' is not registered with the entity memory cache.");
    }

    public bool IsRegistered<TEntity>()
        where TEntity : class
        => _entries.ContainsKey(typeof(TEntity));

    public async Task ReloadAsync<TEntity>(CancellationToken cancellationToken = default)
        where TEntity : class
        => await ReloadAsync(typeof(TEntity), cancellationToken).ConfigureAwait(false);

    public async Task ReloadAsync(Type entityType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entityType);

        if (_entries.TryGetValue(entityType, out var cache))
        {
            await cache.ReloadAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        throw new KeyNotFoundException(
            $"Entity type '{entityType.FullName}' is not registered with the entity memory cache.");
    }

    public async Task ReloadAllAsync(CancellationToken cancellationToken = default)
    {
        var entries = _entries.Values.ToArray();

        await ReloadThrottle.RunAsync(
            entries,
            _serviceOptions.ReloadAllMaxDegreeOfParallelism,
            static (cache, token) => cache.ReloadAsync(token),
            cancellationToken).ConfigureAwait(false);
    }

    public void Invalidate<TEntity>()
        where TEntity : class
        => Invalidate(typeof(TEntity));

    public void Invalidate(Type entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);

        if (!_entries.TryGetValue(entityType, out var cache)
            || !_registrations.TryGetValue(entityType, out var registration))
        {
            throw new KeyNotFoundException(
                $"Entity type '{entityType.FullName}' is not registered with the entity memory cache.");
        }

        Invalidated?.Invoke(
            this,
            new EntityCacheInvalidatedEventArgs
            {
                EntityType = entityType,
                Timestamp = DateTimeOffset.UtcNow,
            });

        cache.Invalidate(registration.InvalidationMode);
    }

    private static void ValidateDiLoaders(IServiceProvider serviceProvider, EntityCacheOptions options)
    {
        var serviceChecker = serviceProvider.GetService<IServiceProviderIsService>();
        if (serviceChecker is null)
        {
            return;
        }

        var missing = options.Registrations
            .Where(registration => registration.UsesDiLoader)
            .Where(registration => !serviceChecker.IsService(
                typeof(IEntityLoader<>).MakeGenericType(registration.EntityType)))
            .Select(registration => registration.EntityType.FullName)
            .ToList();

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "The following cache entities have no IEntityLoader<T> registered in DI: " +
                string.Join(", ", missing) +
                ". Register a loader (e.g. services.AddSingleton<IEntityLoader<T>, TLoader>()) " +
                "or configure one with WithLoader(...).");
        }
    }

    public void Dispose()
    {
        // 取消生命周期令牌，让在途的后台刷新尽快退出。
        // 指标（Meter）由 DI 容器持有并负责释放，这里不重复释放。
        _lifetimeSource.Cancel();
        _lifetimeSource.Dispose();
    }
}
