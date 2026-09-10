using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace MemoryCache.Redis;

/// <summary>
/// 基于 Redis 的实体快照存储（两级缓存中的 L2）。
/// </summary>
/// <typeparam name="TEntity">实体类型。</typeparam>
internal sealed class RedisEntitySnapshotStore<TEntity> : IRedisEntitySnapshotStore<TEntity>
    where TEntity : class
{
    /// <summary>快照格式版本；读到不兼容的版本按“未命中”处理并回源。</summary>
    private const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer _connection;
    private readonly RedisEntityCacheOptions _options;
    private readonly ILogger<RedisEntitySnapshotStore<TEntity>>? _logger;
    private readonly string _key;
    private readonly string _lockKey;

    public RedisEntitySnapshotStore(
        IConnectionMultiplexer connection,
        RedisEntityCacheOptions options,
        ILogger<RedisEntitySnapshotStore<TEntity>>? logger = null)
    {
        _connection = connection;
        _options = options;
        _logger = logger;
        _key = options.KeyPrefix + options.BuildEntityKey(typeof(TEntity));
        _lockKey = _key + ":lock";
    }

    public async Task<RedisEntitySnapshot<TEntity>?> TryReadAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = await Database.StringGetAsync(_key).ConfigureAwait(false);
            if (payload.IsNullOrEmpty)
            {
                return null;
            }

            var envelope = JsonSerializer.Deserialize<SnapshotEnvelope>(payload!, SerializerOptions);
            if (envelope?.Items is null || envelope.SchemaVersion != SchemaVersion)
            {
                _logger?.LogWarning(
                    "Shared snapshot '{Key}' has an incompatible schema version ({Version}); " +
                    "it is treated as a miss.",
                    _key,
                    envelope?.SchemaVersion);
                return null;
            }

            return new RedisEntitySnapshot<TEntity>
            {
                Items = envelope.Items,
                LoadedAt = envelope.LoadedAt,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(
                ex,
                "Reading shared snapshot '{Key}' failed; it is treated as a miss and the " +
                "database is used instead.",
                _key);
            return null;
        }
    }

    public async Task WriteAsync(
        IReadOnlyCollection<TEntity> items,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var envelope = new SnapshotEnvelope
            {
                SchemaVersion = SchemaVersion,
                LoadedAt = DateTimeOffset.UtcNow,
                Items = items as TEntity[] ?? items.ToArray(),
            };

            var payload = JsonSerializer.Serialize(envelope, SerializerOptions);
            await Database
                .StringSetAsync(_key, payload, expiry: GetTimeToLive())
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(
                ex,
                "Writing shared snapshot '{Key}' failed; the in-process snapshot is still used.",
                _key);
        }
    }

    public async Task<RedisRefreshLockResult> TryAcquireRefreshLockAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var acquired = await Database
                .StringSetAsync(
                    _lockKey,
                    _options.InstanceId,
                    expiry: _options.RefreshLockTimeout,
                    when: When.NotExists)
                .ConfigureAwait(false);

            return acquired ? RedisRefreshLockResult.Acquired : RedisRefreshLockResult.Busy;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(
                ex,
                "Acquiring the refresh lock for shared snapshot '{Key}' failed; the database is " +
                "queried directly.",
                _key);
            return RedisRefreshLockResult.Unavailable;
        }
    }

    public async Task ReleaseRefreshLockAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await Database.KeyDeleteAsync(_lockKey).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(
                ex,
                "Releasing the refresh lock for shared snapshot '{Key}' failed; it expires by TTL.",
                _key);
        }
    }

    private TimeSpan GetTimeToLive()
    {
        var jitter = _options.TimeToLiveJitter;
        if (jitter <= TimeSpan.Zero)
        {
            return _options.EntryTimeToLive;
        }

        var maxJitterMilliseconds = (int)Math.Min(jitter.TotalMilliseconds, int.MaxValue);
        return _options.EntryTimeToLive +
            TimeSpan.FromMilliseconds(Random.Shared.Next(0, maxJitterMilliseconds));
    }

    private IDatabase Database => _connection.GetDatabase();

    private sealed class SnapshotEnvelope
    {
        public int SchemaVersion { get; set; }

        public DateTimeOffset LoadedAt { get; set; }

        public TEntity[]? Items { get; set; }
    }
}
