using System.Text.RegularExpressions;
using MySqlConnector;

namespace MemoryCache.Samples.Shared;

/// <summary>
/// 示例用的建库 / 建表 / 种子数据辅助（幂等）。
/// </summary>
/// <remarks>
/// 目标：让示例在一台只有 MySQL 的机器上开箱即跑，同时演示"实体类对应哪张表"是可以自定义的——
/// 把表名通过环境变量 <c>MEMORY_CACHE_MYSQL_TABLE</c> 传进来即可（默认 <c>job_config</c>）。
/// 换成你自己的表时，同步修改示例实体类（<c>JobConfig</c>）与这里的建表语句即可。
/// </remarks>
public static class SampleSchema
{
    /// <summary>默认表名。</summary>
    public const string DefaultTable = "job_config";

    private static readonly Regex SafeName = new("^[A-Za-z0-9_]+$", RegexOptions.Compiled);

    /// <summary>
    /// 解析示例使用的表名：优先取环境变量 <c>MEMORY_CACHE_MYSQL_TABLE</c>，否则用默认值。
    /// </summary>
    public static string ResolveTableName()
    {
        var configured = Environment.GetEnvironmentVariable("MEMORY_CACHE_MYSQL_TABLE");
        var tableName = string.IsNullOrWhiteSpace(configured) ? DefaultTable : configured.Trim();

        if (!SafeName.IsMatch(tableName))
        {
            throw new ArgumentException(
                $"表名只能包含字母、数字和下划线，实际为“{tableName}”。",
                nameof(configured));
        }

        return tableName;
    }

    /// <summary>
    /// 确保连接串里的数据库与指定的表存在（不存在则创建），表为空时写入一行示例数据。
    /// </summary>
    /// <param name="connectionString">MySQL 连接串。</param>
    /// <param name="tableName">示例表名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task EnsureAsync(
        string connectionString,
        string tableName,
        CancellationToken cancellationToken = default)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString);
        var database = builder.Database;

        if (!string.IsNullOrWhiteSpace(database))
        {
            // 建库需要先连到服务器（不指定 Database）。
            var serverBuilder = new MySqlConnectionStringBuilder(connectionString) { Database = "" };
            await using var serverConnection = new MySqlConnection(serverBuilder.ConnectionString);
            await serverConnection.OpenAsync(cancellationToken);

            await using var createDatabase = serverConnection.CreateCommand();
            createDatabase.CommandText =
                $"CREATE DATABASE IF NOT EXISTS `{database}` DEFAULT CHARACTER SET utf8mb4";
            await createDatabase.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var createTable = connection.CreateCommand())
        {
            createTable.CommandText = $"""
                CREATE TABLE IF NOT EXISTS `{tableName}` (
                  `GROUP` VARCHAR(50) NOT NULL,
                  `JOB_KEYNAME` VARCHAR(50) NOT NULL,
                  `JOB_DESC` VARCHAR(100) NULL,
                  `TRIGGER_KEYNAME` VARCHAR(50) NOT NULL,
                  `TRIGGER_DESC` VARCHAR(100) NULL,
                  `CRON` VARCHAR(50) NOT NULL,
                  `CRON_DESC` VARCHAR(100) NULL,
                  `IS_ENABLE` CHAR(1) NOT NULL DEFAULT 'N',
                  PRIMARY KEY (`GROUP`, `JOB_KEYNAME`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
                """;
            await createTable.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(*) FROM `{tableName}`";
        var existing = Convert.ToInt64(await countCommand.ExecuteScalarAsync(cancellationToken));

        if (existing > 0)
        {
            return;
        }

        await using var seed = connection.CreateCommand();
        seed.CommandText = $"""
            INSERT INTO `{tableName}`
              (`GROUP`, `JOB_KEYNAME`, `JOB_DESC`, `TRIGGER_KEYNAME`, `TRIGGER_DESC`, `CRON`, `CRON_DESC`, `IS_ENABLE`)
            VALUES
              ('Group1', 'TestJob', '示例任务', 'testname', '示例触发器', 'aas', '示例 cron 表达式', 'N')
            """;
        await seed.ExecuteNonQueryAsync(cancellationToken);
    }
}
