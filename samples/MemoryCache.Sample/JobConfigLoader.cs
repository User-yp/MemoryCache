using MemoryCache.Abstractions;
using MySqlConnector;

namespace MemoryCache.Sample;

/// <summary>
/// 使用 MySqlConnector 全量加载 <c>job_config</c> 表。
/// </summary>
public sealed class JobConfigLoader(string connectionString) : IEntityLoader<JobConfig>
{
    private int _loadCount;

    public int LoadCount => Volatile.Read(ref _loadCount);

    public async Task<IReadOnlyCollection<JobConfig>> LoadAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _loadCount);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT `GROUP`, JOB_KEYNAME, JOB_DESC, TRIGGER_KEYNAME, TRIGGER_DESC, " +
            "CRON, CRON_DESC, IS_ENABLE FROM job_config";

        var items = new List<JobConfig>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new JobConfig
            {
                Group = reader.GetString(0),
                JobKeyName = reader.GetString(1),
                JobDesc = reader.IsDBNull(2) ? null : reader.GetString(2),
                TriggerKeyName = reader.GetString(3),
                TriggerDesc = reader.IsDBNull(4) ? null : reader.GetString(4),
                Cron = reader.GetString(5),
                CronDesc = reader.IsDBNull(6) ? null : reader.GetString(6),
                IsEnabled = string.Equals(reader.GetString(7), "Y", StringComparison.OrdinalIgnoreCase),
            });
        }

        return items;
    }
}
