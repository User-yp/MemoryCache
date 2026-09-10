using MemoryCache.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MemoryCache;

/// <summary>
/// 按照各实体的刷新周期，在后台定时刷新缓存。
/// </summary>
internal sealed class EntityCachePeriodicRefresher : BackgroundService
{
    private readonly IEntityCacheService _cacheService;
    private readonly ILogger<EntityCachePeriodicRefresher>? _logger;
    private readonly IReadOnlyList<EntityCacheRegistration> _periodicRegistrations;
    private readonly int _maxDegreeOfParallelism;

    public EntityCachePeriodicRefresher(
        IServiceProvider serviceProvider,
        EntityCacheOptions cacheOptions)
    {
        _cacheService = serviceProvider.GetRequiredService<IEntityCacheService>();
        _logger = serviceProvider.GetService<ILogger<EntityCachePeriodicRefresher>>();
        _maxDegreeOfParallelism = cacheOptions.ServiceOptions.ReloadAllMaxDegreeOfParallelism;
        _periodicRegistrations = cacheOptions.Registrations
            .Where(registration => registration.RefreshIntervalSeconds > 0)
            .ToArray();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_periodicRegistrations.Count == 0)
        {
            return;
        }

        // 首次执行安排在第一个计时周期；之后的执行时间按各自周期顺延。
        var nextRuns = _periodicRegistrations.ToDictionary(
            registration => registration.EntityType,
            _ => DateTimeOffset.UtcNow);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            var now = DateTimeOffset.UtcNow;
            var dueRegistrations = _periodicRegistrations
                .Where(registration => now >= nextRuns[registration.EntityType])
                .ToArray();

            if (dueRegistrations.Length == 0)
            {
                continue;
            }

            foreach (var registration in dueRegistrations)
            {
                nextRuns[registration.EntityType] =
                    now.AddSeconds(registration.RefreshIntervalSeconds);
            }

            await ReloadThrottle.RunAsync(
                dueRegistrations,
                _maxDegreeOfParallelism,
                (registration, token) => RefreshSafelyAsync(registration.EntityType, token),
                stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RefreshSafelyAsync(Type entityType, CancellationToken cancellationToken)
    {
        try
        {
            await _cacheService.ReloadAsync(entityType, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 服务停止导致的取消：正常退出。
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "Periodic refresh of cache entity '{EntityName}' failed; " +
                "the previous snapshot is kept.",
                entityType.Name);
        }
    }
}
