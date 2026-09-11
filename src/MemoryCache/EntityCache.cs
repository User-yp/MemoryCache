using System.Diagnostics;
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
    private static readonly IReadOnlyList<TEntity> EmptyItems = Array.Empty<TEntity>();

    private readonly EntityCacheRegistration<TEntity> _registration;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EntityCache<TEntity>>? _logger;
    private readonly CacheKeyAccessor<TEntity>? _keyAccessor;
    private readonly CacheMetrics _metrics;
    private readonly CancellationToken _lifetimeToken;
    private readonly object _stateGate = new();

    private CacheSnapshot<TEntity>? _snapshot;
    private Task? _inFlight;
    private bool _stale;
    private long _staleGeneration;
    private bool _lastReloadFailed;
    private Exception? _lastError;

    public EntityCache(
        IServiceProvider serviceProvider,
        EntityCacheRegistration<TEntity> registration,
        CancellationToken lifetimeToken)
    {
        _registration = registration;
        _scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        _logger = serviceProvider.GetService<ILogger<EntityCache<TEntity>>>();
        _metrics = serviceProvider.GetRequiredService<CacheMetrics>();
        _lifetimeToken = lifetimeToken;
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
                // 单飞：本次加载沿用“发起者”的取消令牌，后续加入的调用方
                // 只能取消自己的等待，不会改变已经在跑的加载。
                // 同时记下加载开始时的失效代次：期间新发生的失效不会被它清掉。
                _inFlight = ExecuteReloadAsync(cancellationToken, _staleGeneration);
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
            _staleGeneration++;
        }
    }

    private void InvalidateCore(InvalidationMode invalidationMode)
    {
        MarkStale();

        if (invalidationMode == InvalidationMode.ReloadInBackground)
        {
            _ = RefreshAfterInvalidationAsync();
        }
    }

    /// <summary>
    /// 失效后的后台刷新。
    /// </summary>
    /// <remarks>
    /// 先等当前在途的刷新结束；如果那次刷新开始于本次失效之前（它的数据可能早于本次变更），
    /// 再补一次刷新，保证“失效”不会因为撞上并发刷新而被吞掉。
    /// </remarks>
    private async Task RefreshAfterInvalidationAsync()
    {
        if (!await TryReloadAsync().ConfigureAwait(false))
        {
            // 刷新失败：错误已记录在 LastError，交给 EnsureFreshAsync / 定时刷新重试，
            // 这里不立即重试，避免数据源故障时持续打库。
            return;
        }

        if (!IsStale)
        {
            return;
        }

        await TryReloadAsync().ConfigureAwait(false);
    }

    private async Task<bool> TryReloadAsync()
    {
        try
        {
            await ReloadAsync(_lifetimeToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            // 失败原因已由 ExecuteReloadAsync 记录并告警，这里只需要知道“没成功”。
            return false;
        }
    }

    private async Task ExecuteReloadAsync(
        CancellationToken cancellationToken,
        long staleGeneration)
    {
        // 让出一次执行权：ExecuteReloadAsync 是在 ReloadAsync 的锁内被调用的，
        // 若加载器同步完成，整段刷新（含状态发布与 Reloaded 回调）都会跑在
        // 持有 _stateGate 的调用线程上——回调稍慢就会卡住其他线程的状态操作。
        // 先让出，保证回调与状态发布都不在调用方的锁内执行。
        await Task.Yield();

        var previousVersion = Version;
        CacheSnapshot<TEntity>? newSnapshot = null;
        Exception? error = null;
        var cancelled = false;
        var stopwatch = Stopwatch.StartNew();

        // 实体级单次刷新超时：与调用方令牌链接，超时后中止本次取数。
        var loadTimeout = _registration.LoadTimeout;
        using var timeoutSource = loadTimeout is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (loadTimeout is { } timeout)
        {
            timeoutSource!.CancelAfter(timeout);
        }

        var loadToken = timeoutSource?.Token ?? cancellationToken;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            newSnapshot = await LoadSnapshotAsync(previousVersion + 1, loadToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            // 刷新被调用方取消：这不是“加载失败”，保留旧快照，也不写入
            // LastError、不计失败、不触发 Reloaded，避免让缓存看起来出了问题。
            cancelled = true;
            error = ex;
        }
        catch (OperationCanceledException ex) when (timeoutSource is { IsCancellationRequested: true })
        {
            // 加载超时不是调用方取消，而是数据源太慢：按“刷新失败”处理，
            // 让 LastError、指标与 EnsureFreshAsync 的重试都能看到它。
            error = new TimeoutException(
                $"Reload of cache entity '{_registration.Name}' exceeded the configured " +
                $"load timeout of {_registration.LoadTimeout}.",
                ex);
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            stopwatch.Stop();
        }

        if (!cancelled)
        {
            lock (_stateGate)
            {
                if (error is null)
                {
                    Volatile.Write(ref _snapshot, newSnapshot);

                    // 只有本次加载开始之后没有新的失效，才清除失效标记：
                    // 否则“加载期间发生的失效”会被这次（可能更旧的）加载结果吞掉。
                    if (_staleGeneration == staleGeneration)
                    {
                        _stale = false;
                    }

                    _lastReloadFailed = false;
                    _lastError = null;
                }
                else
                {
                    _lastReloadFailed = true;
                    _lastError = error;
                }
            }

            _metrics.RecordReload(
                _registration.Name,
                success: error is null,
                stopwatch.Elapsed.TotalSeconds);

            // 指标先记录，再触发事件：订阅者即便抛异常也不会让指标丢数。
            RaiseReloaded(previousVersion, error);

            if (error is not null)
            {
                _logger?.LogWarning(
                    error,
                    "Reload of cache entity '{EntityName}' failed; the previous snapshot is kept.",
                    _registration.Name);
            }
        }

        if (error is not null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    private async Task<CacheSnapshot<TEntity>> LoadSnapshotAsync(
        long newVersion,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();

        var loader = ResolveLoader(scope.ServiceProvider);

        // 允许第三方包装饰加载器（例如 MemoryCache.Redis 先读 Redis、未命中再回源数据库）。
        foreach (var decorator in scope.ServiceProvider.GetServices<IEntityLoaderDecorator<TEntity>>())
        {
            loader = decorator.Decorate(loader)
                ?? throw new InvalidOperationException(
                    $"Loader decorator '{decorator.GetType().Name}' returned null for cache " +
                    $"entity '{_registration.Name}'.");
        }

        var loadedItems = await loader.LoadAsync(cancellationToken).ConfigureAwait(false)
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
            // 以只读包装发布，调用方无法把它还原成可写数组。
            Items = Array.AsReadOnly(items),
            Index = index,
            Version = newVersion,
            LoadedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// 解析使用方配置的原始加载器（未经任何装饰）。
    /// </summary>
    private IEntityLoader<TEntity> ResolveLoader(IServiceProvider scopeProvider)
        => _registration.LoaderFactory is { } loaderFactory
            ? loaderFactory(scopeProvider)
            : scopeProvider.GetRequiredService<IEntityLoader<TEntity>>();

    /// <summary>
    /// 用原始加载器取数（绕开装饰器），供扩展组件回填共享存储使用。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    internal async Task<IReadOnlyList<TEntity>> LoadFromSourceAsync(
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var loader = ResolveLoader(scope.ServiceProvider);

        var items = await loader.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Loader for cache entity '{_registration.Name}' returned null.");

        return items as IReadOnlyList<TEntity> ?? items.ToArray();
    }

    private IReadOnlyDictionary<object, TEntity>? BuildIndex(TEntity[] items)
    {
        if (_keyAccessor is null)
        {
            return null;
        }

        var comparer = _keyAccessor.IsComposite ? CacheKeyComparer.Instance : null;
        var index = new Dictionary<object, TEntity>(comparer);
        var nullKeyCount = 0;

        foreach (var item in items)
        {
            var key = _keyAccessor.Select(item);
            if (key is null)
            {
                if (_registration.NullKeyPolicy == NullKeyPolicy.Throw)
                {
                    throw new InvalidOperationException(
                        $"Cache key selector for entity '{_registration.Name}' returned null for " +
                        "one of the loaded items.");
                }

                // 默认策略：保留在快照中，只是不进键索引，
                // 避免一条脏数据让整表刷新失败、缓存永远停在旧版本。
                nullKeyCount++;
                continue;
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

        if (nullKeyCount > 0)
        {
            _logger?.LogWarning(
                "Left {ItemCount} item(s) with a null cache key out of the key index while " +
                "loading entity '{EntityName}'; they stay in the snapshot but cannot be " +
                "reached through key lookups.",
                nullKeyCount,
                _registration.Name);
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
        var args = new EntityCacheReloadedEventArgs<TEntity>
        {
            PreviousVersion = previousVersion,
            NewVersion = error is null ? currentSnapshot?.Version ?? previousVersion : previousVersion,
            Failed = error is not null,
            Error = error,
        };

        // 逐个调用订阅者：单个订阅者抛异常不应该影响其他订阅者，
        // 也不应该掩盖加载器本身的错误、或让调用方看到订阅者的异常。
        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<EntityCacheReloadedEventArgs<TEntity>>)subscriber)(this, args);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    ex,
                    "A Reloaded handler for cache entity '{EntityName}' threw an exception; " +
                    "it was ignored.",
                    _registration.Name);
            }
        }
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
