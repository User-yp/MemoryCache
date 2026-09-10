using System.Reflection;
using MemoryCache.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MemoryCache.Redis;

/// <summary>
/// 共享快照维护实现：回源数据库、回填 Redis、刷新本进程缓存并广播失效。
/// </summary>
internal sealed class RedisEntitySnapshotMaintenance : IRedisEntitySnapshotMaintenance
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEntityCacheService _cacheService;
    private readonly IEntitySourceLoader _sourceLoader;
    private readonly IRedisEntityInvalidationChannel _channel;
    private readonly ILogger<RedisEntitySnapshotMaintenance>? _logger;

    public RedisEntitySnapshotMaintenance(
        IServiceScopeFactory scopeFactory,
        IEntityCacheService cacheService,
        IEntitySourceLoader sourceLoader,
        IRedisEntityInvalidationChannel channel,
        ILogger<RedisEntitySnapshotMaintenance>? logger = null)
    {
        _scopeFactory = scopeFactory;
        _cacheService = cacheService;
        _sourceLoader = sourceLoader;
        _channel = channel;
        _logger = logger;
    }

    public async Task RefreshFromSourceAsync<TEntity>(CancellationToken cancellationToken = default)
        where TEntity : class
    {
        // 用“原始取数”入口：绕开 Redis 装饰器，保证读到的是数据库的最新数据。
        var items = await _sourceLoader.LoadAsync<TEntity>(cancellationToken).ConfigureAwait(false);

        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRedisEntitySnapshotStore<TEntity>>();
        await store.WriteAsync(items, cancellationToken).ConfigureAwait(false);

        // 本进程立即刷新：这次刷新的加载器会从刚写入的共享快照读取，不会重复查库。
        await _cacheService.ReloadAsync<TEntity>(cancellationToken).ConfigureAwait(false);

        // 通知其它实例：它们在刷新时同样从共享快照读取。
        await _channel.PublishAsync(typeof(TEntity), cancellationToken).ConfigureAwait(false);

        _logger?.LogInformation(
            "Refilled and broadcast the shared snapshot of cache entity '{EntityName}' ({Count} items).",
            typeof(TEntity).Name,
            items.Count);
    }

    public Task RefreshFromSourceAsync(Type entityType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entityType);

        var method = typeof(RedisEntitySnapshotMaintenance)
            .GetMethod(
                nameof(RefreshFromSourceAsync),
                bindingAttr: BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                genericParameterCount: 1,
                types: [typeof(CancellationToken)],
                modifiers: null)
            ?? throw new MissingMethodException(
                typeof(RedisEntitySnapshotMaintenance).FullName,
                nameof(RefreshFromSourceAsync));

        try
        {
            return (Task)method.MakeGenericMethod(entityType).Invoke(this, [cancellationToken])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }
}
