using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace MemoryCache.Redis;

/// <summary>
/// 基于 Redis Pub/Sub 的失效广播发布端。
/// </summary>
internal sealed class RedisEntityInvalidationChannel : IRedisEntityInvalidationChannel
{
    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer _connection;
    private readonly RedisEntityCacheOptions _options;
    private readonly ILogger<RedisEntityInvalidationChannel>? _logger;

    public RedisEntityInvalidationChannel(
        IConnectionMultiplexer connection,
        RedisEntityCacheOptions options,
        ILogger<RedisEntityInvalidationChannel>? logger = null)
    {
        _connection = connection;
        _options = options;
        _logger = logger;
    }

    public async Task PublishAsync(Type entityType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entityType);

        var message = new RedisEntityInvalidationMessage
        {
            EntityType = entityType.AssemblyQualifiedName ?? entityType.FullName ?? entityType.Name,
            Origin = _options.InstanceId,
            Timestamp = DateTimeOffset.UtcNow,
        };

        try
        {
            var payload = JsonSerializer.Serialize(message, SerializerOptions);
            await _connection
                .GetSubscriber()
                .PublishAsync(RedisChannel.Literal(_options.InvalidationChannel), payload)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(
                ex,
                "Publishing the invalidation broadcast for cache entity '{EntityName}' failed; " +
                "other instances converge when the shared snapshot TTL expires or on their " +
                "periodic refresh.",
                entityType.Name);
        }
    }
}
