using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.ExceptionServices;
using MemoryCache.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MemoryCache;

/// <summary>
/// 内部注册表使用的缓存项非泛型视图。
/// </summary>
internal interface IEntityCache
{
    Type EntityType { get; }

    int Count { get; }

    Task ReloadAsync(CancellationToken cancellationToken);

    Task EnsureFreshAsync(CancellationToken cancellationToken);

    void Invalidate(InvalidationMode invalidationMode);
}

/// <summary>
/// 单个实体类型的无锁快照缓存项。
/// </summary>
/// <typeparam name="TEntity">被缓存的实体类型。</typeparam>
/// <remarks>
/// 读者通过 volatile 读取观察不可变快照；写者构建全新快照并原子发布。
/// 并发的刷新请求会被单飞合并，刷新失败时保留上一份快照。
/// </remarks>
internal sealed class EntityCache<TEntity> : IEntityCache<TEntity>, IEntityCache
    where TEntity : class
{
    private static readonly TEntity[] EmptyItems = [];

    private readonly EntityCacheRegistration<TEntity> _registration;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EntityCache<TEntity>>? _logger;
    private readonly CacheKeyAccessor<TEntity>? _keyAccessor;
    private readonly CacheMetrics _metrics;
    private readonly object _stateGate = new();

    private CacheSnapshot<TEntity>? _snapshot;
    private Task? _inFlight;
    private bool _stale;
    private bool _lastReloadFailed;
    private Exception? _lastError;

    public EntityCache(
        IServiceProvider serviceProvider,
        EntityCacheRegistration<TEntity> registration)
    {
        _registration = registration;
        _scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        _logger = serviceProvider.GetService<ILogger<EntityCache<TEntity>>>();
        _metrics = serviceProvider.GetRequiredService<EntityCacheOptions>().Metrics;
        _keyAccessor = CacheKeyAccessorFactory.Create(
            registration.KeyPropertyName,
            registration.KeySelector);
    }

    public Type EntityType
        => typeof(TEntity);

    public long Version
    {
        get
        {
            var snapshot = Volatile.Read(ref _snapshot);
            return snapshot?.Version ?? 0;
        }
    }

    public int Count
    {
        get
        {
            var snapshot = Volatile.Read(ref _snapshot);
            return snapshot?.Items.Count ?? 0;
        }
    }

    public bool IsStale
    {
        get
        {
            lock (_stateGate)
            {
                return _stale;
            }
        }
    }

    public DateTimeOffset? LoadedAt
    {
        get
        {
            var snapshot = Volatile.Read(ref _snapshot);
            return snapshot?.LoadedAt;
        }
    }

    public Exception? LastError
    {
        get
        {
            lock (_stateGate)
            {
                return _lastError;
            }
        }
    }

    public IReadOnlyList<TEntity> GetSnapshot()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot?.Items ?? EmptyItems;
    }

    public IQueryable<TEntity> AsQueryable()
        => GetSnapshot().AsQueryable();

    public bool ContainsKey(object key)
        => TryGetValue(key, out _);

    public bool TryGetValue(object key, out TEntity? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        EnsureKeyConfigured();

        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot?.Index is null)
        {
            value = null;
            return false;
        }

        return snapshot.Index.TryGetValue(key, out value);
    }

    public TEntity? GetByKey(object key)
    {
        EnsureKeyConfigured();
        return TryGetValue(key, out var value) ? value : null;
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        Task inFlight;
        lock (_stateGate)
        {
            if (_inFlight is null || _inFlight.IsCompleted)
            {
                _inFlight = ExecuteReloadAsync();
            }

            inFlight = _inFlight;
        }

        await inFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureFreshAsync(CancellationToken cancellationToken = default)
    {
        var needsReload = false;
        lock (_stateGate)
        {
            needsReload = Volatile.Read(ref _snapshot) is null || _stale || _lastReloadFailed;
        }

        if (!needsReload)
        {
            return;
        }

        await ReloadAsync(cancellationToken).ConfigureAwait(false);
    }

    public event EventHandler<EntityCacheReloadedEventArgs<TEntity>>? Reloaded;

    void IEntityCache.Invalidate(InvalidationMode invalidationMode)
        => InvalidateCore(invalidationMode);

    internal void MarkStale()
    {
        lock (_stateGate)
        {
            _stale = true;
        }
    }

    private void InvalidateCore(InvalidationMode invalidationMode)
    {
        MarkStale();

        if (invalidationMode == InvalidationMode.ReloadInBackground)
        {
            _ = ReloadAsync(CancellationToken.None)
                .ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
        }
    }

    private async Task ExecuteReloadAsync()
    {
        var previousVersion = Version;
        CacheSnapshot<TEntity>? newSnapshot = null;
        Exception? error = null;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            newSnapshot = await LoadSnapshotAsync(previousVersion + 1).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            stopwatch.Stop();
        }

        lock (_stateGate)
        {
            if (error is null)
            {
                Volatile.Write(ref _snapshot, newSnapshot);
                _stale = false;
                _lastReloadFailed = false;
                _lastError = null;
            }
            else
            {
                _lastReloadFailed = true;
                _lastError = error;
            }
        }

        RaiseReloaded(previousVersion, error);
        _metrics.RecordReload(
            _registration.Name,
            success: error is null,
            stopwatch.Elapsed.TotalSeconds);

        if (error is not null)
        {
            _logger?.LogWarning(
                error,
                "Reload of cache entity '{EntityName}' failed; the previous snapshot is kept.",
                _registration.Name);
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    private async Task<CacheSnapshot<TEntity>> LoadSnapshotAsync(long newVersion)
    {
        using var scope = _scopeFactory.CreateScope();

        IEntityLoader<TEntity> loader = _registration.LoaderFactory is { } loaderFactory
            ? loaderFactory(scope.ServiceProvider)
            : scope.ServiceProvider.GetRequiredService<IEntityLoader<TEntity>>();

        var loadedItems = await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Loader for cache entity '{_registration.Name}' returned null.");

        var items = loadedItems.ToArray();

        if (items.LongLength > _registration.CapacityWarningThreshold)
        {
            _logger?.LogWarning(
                "Cache entity '{EntityName}' holds {ItemCount} items, exceeding the " +
                "capacity warning threshold of {Threshold}.",
                _registration.Name,
                items.LongLength,
                _registration.CapacityWarningThreshold);
        }

        var index = BuildIndex(items);

        return new CacheSnapshot<TEntity>
        {
            Items = items,
            Index = index,
            Version = newVersion,
            LoadedAt = DateTimeOffset.UtcNow,
        };
    }

    private IReadOnlyDictionary<object, TEntity>? BuildIndex(TEntity[] items)
    {
        if (_keyAccessor is null)
        {
            return null;
        }

        var comparer = _keyAccessor.IsComposite ? CacheKeyComparer.Instance : null;
        var index = new Dictionary<object, TEntity>(comparer);

        foreach (var item in items)
        {
            var key = _keyAccessor.Select(item);
            if (key is null)
            {
                throw new InvalidOperationException(
                    $"Cache key selector for entity '{_registration.Name}' returned null for " +
                    $"one of the loaded items.");
            }

            if (index.TryGetValue(key, out var existing))
            {
                switch (_registration.DuplicateKeyPolicy)
                {
                    case DuplicateKeyPolicy.Throw:
                        throw new InvalidOperationException(
                            $"Duplicate cache key '{key}' found while loading entity " +
                            $"'{_registration.Name}'.");
                    case DuplicateKeyPolicy.KeepFirst:
                        _logger?.LogWarning(
                            "Duplicate cache key '{Key}' found while loading entity " +
                            "'{EntityName}'; the first item is kept.",
                            key,
                            _registration.Name);
                        continue;
                    case DuplicateKeyPolicy.LogWarningAndKeepLast:
                    default:
                        _logger?.LogWarning(
                            "Duplicate cache key '{Key}' found while loading entity " +
                            "'{EntityName}'; the last item is kept.",
                            key,
                            _registration.Name);
                        index[key] = item;
                        break;
                }
            }
            else
            {
                index.Add(key, item);
            }
        }

        return index;
    }

    private void RaiseReloaded(long previousVersion, Exception? error)
    {
        var handler = Reloaded;
        if (handler is null)
        {
            return;
        }

        var currentSnapshot = Volatile.Read(ref _snapshot);
        handler(
            this,
            new EntityCacheReloadedEventArgs<TEntity>
            {
                PreviousVersion = previousVersion,
                NewVersion = error is null ? currentSnapshot?.Version ?? previousVersion : previousVersion,
                Failed = error is not null,
                Error = error,
            });
    }

    private void EnsureKeyConfigured()
    {
        if (_keyAccessor is null)
        {
            throw new NotSupportedException(
                $"Entity '{_registration.Name}' has no configured cache key; " +
                "key-based query APIs are not available.");
        }
    }
}
