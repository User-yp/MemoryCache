using System.Text.Json;
using MemoryCache.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace MemoryCache.Redis;

/// <summary>
/// 订阅失效广播频道，把收到的通知转成对应实体的进程内缓存失效。
/// </summary>
internal sealed class RedisEntityInvalidationSubscriber : BackgroundService
{
    private readonly IConnectionMultiplexer _connection;
    private readonly IEntityCacheService _cacheService;
    private readonly RedisEntityCacheOptions _options;
    private readonly ILogger<RedisEntityInvalidationSubscriber>? _logger;

    public RedisEntityInvalidationSubscriber(
        IConnectionMultiplexer connection,
        IEntityCacheService cacheService,
        RedisEntityCacheOptions options,
        ILogger<RedisEntityInvalidationSubscriber>? logger = null)
    {
        _connection = connection;
        _cacheService = cacheService;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channel = RedisChannel.Literal(_options.InvalidationChannel);
        var subscriber = _connection.GetSubscriber();

        try
        {
            await subscriber.SubscribeAsync(channel, OnMessageReceived).ConfigureAwait(false);
            _logger?.LogInformation(
                "Subscribed to the entity cache invalidation channel '{Channel}'.",
                _options.InvalidationChannel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(
                ex,
                "Subscribing to the entity cache invalidation channel '{Channel}' failed; this " +
                "instance only relies on its own refreshes and periodic refresh.",
                _options.InvalidationChannel);
            return;
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
        finally
        {
            try
            {
                await subscriber.UnsubscribeAsync(channel).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Unsubscribing from the invalidation channel failed.");
            }
        }
    }

    private void OnMessageReceived(RedisChannel channel, RedisValue value)
    {
        RedisEntityInvalidationMessage? message;

        try
        {
            message = JsonSerializer.Deserialize<RedisEntityInvalidationMessage>(
                value.ToString(),
                RedisEntityInvalidationChannel.SerializerOptions);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Received a malformed invalidation broadcast; it was ignored.");
            return;
        }

        if (message is null || string.IsNullOrWhiteSpace(message.EntityType))
        {
            return;
        }

        if (string.Equals(message.Origin, _options.InstanceId, StringComparison.Ordinal))
        {
            // 自己发起的变更：本进程已经刷新过，无需重复处理。
            return;
        }

        var entityType = ResolveEntityType(message.EntityType);
        if (entityType is null)
        {
            _logger?.LogWarning(
                "Received an invalidation broadcast for unknown entity type '{EntityType}'; " +
                "it was ignored.",
                message.EntityType);
            return;
        }

        if (!_cacheService.RegisteredTypes.Contains(entityType))
        {
            _logger?.LogDebug(
                "Ignoring invalidation broadcast for entity '{EntityName}': it is not registered " +
                "with this instance.",
                entityType.Name);
            return;
        }

        // 按该实体的失效模式处理（默认 ReloadInBackground：标记失效并后台刷新，
        // 刷新时从共享快照读取，不会重复查库）。
        _cacheService.Invalidate(entityType);
    }

    private Type? ResolveEntityType(string typeName)
    {
        var resolved = Type.GetType(typeName, throwOnError: false);
        if (resolved is not null)
        {
            return resolved;
        }

        // 退化匹配：程序集版本变化会导致限定名对不上，此时按全名匹配已注册类型。
        return _cacheService.RegisteredTypes.FirstOrDefault(registered =>
            string.Equals(registered.FullName, typeName, StringComparison.Ordinal) ||
            string.Equals(registered.Name, typeName, StringComparison.Ordinal));
    }
}
