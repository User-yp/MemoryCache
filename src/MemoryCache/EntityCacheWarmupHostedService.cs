using MemoryCache.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MemoryCache;

/// <summary>
/// 应用启动后自动预热全部已注册实体缓存的托管服务。
/// </summary>
internal sealed class EntityCacheWarmupHostedService : IHostedService
{
    private readonly IEntityCacheService _cacheService;
    private readonly ILogger<EntityCacheWarmupHostedService>? _logger;
    private readonly EntityCacheServiceOptions _options;

    public EntityCacheWarmupHostedService(
        IServiceProvider serviceProvider,
        EntityCacheOptions cacheOptions)
    {
        _cacheService = serviceProvider.GetRequiredService<IEntityCacheService>();
        _logger = serviceProvider.GetService<ILogger<EntityCacheWarmupHostedService>>();
        _options = cacheOptions.ServiceOptions;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.WarmupOnStartup)
        {
            return;
        }

        using var timeoutSource = _options.WarmupTimeout > TimeSpan.Zero
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;

        if (timeoutSource is not null)
        {
            timeoutSource.CancelAfter(_options.WarmupTimeout);
        }

        var warmupToken = timeoutSource?.Token ?? cancellationToken;

        try
        {
            await _cacheService.ReloadAllAsync(warmupToken).ConfigureAwait(false);
            _logger?.LogInformation(
                "Entity memory cache warm-up completed for {EntityCount} registered type(s).",
                _cacheService.RegisteredTypes.Count);
        }
        catch (Exception ex)
        {
            // 宿主自身请求的取消（应用正在停止）不属于预热失败，必须继续向上传播。
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            // 预热超时同样按“预热失败”处理：交给 StartupFailurePolicy 决定
            // 是继续启动（Continue）还是快速失败（Throw）。
            var timedOut = ex is OperationCanceledException
                && timeoutSource is { IsCancellationRequested: true };

            if (_options.StartupFailurePolicy == StartupFailurePolicy.Throw)
            {
                if (timedOut)
                {
                    throw new TimeoutException(
                        "Entity memory cache warm-up exceeded the configured timeout of " +
                        $"{_options.WarmupTimeout}.",
                        ex);
                }

                throw;
            }

            if (timedOut)
            {
                _logger?.LogWarning(
                    ex,
                    "Entity memory cache warm-up exceeded the configured timeout of " +
                    "{WarmupTimeout}; the application continues to start. The pending load " +
                    "keeps running in the background.",
                    _options.WarmupTimeout);
            }
            else
            {
                _logger?.LogWarning(
                    ex,
                    "Entity memory cache warm-up failed; the application continues to start.");
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}
