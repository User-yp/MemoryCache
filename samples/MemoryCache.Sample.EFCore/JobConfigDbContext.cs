using MemoryCache.Samples.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MemoryCache.Sample.EFCore;

/// <summary>
/// 面向本地 MySQL <c>quartz</c> 库的 EF Core DbContext。
/// </summary>
public sealed class JobConfigDbContext(DbContextOptions<JobConfigDbContext> options)
    : DbContext(options)
{
    public DbSet<JobConfig> JobConfigs => Set<JobConfig>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<JobConfig>();

        // 表名可由 MEMORY_CACHE_MYSQL_TABLE 自定义，与建表辅助保持一致。
        entity.ToTable(SampleSchema.ResolveTableName());
        entity.HasKey(config => new { config.Group, config.JobKeyName });

        entity.Property(config => config.Group)
            .HasColumnName("GROUP")
            .HasMaxLength(50)
            .IsRequired();
        entity.Property(config => config.JobKeyName)
            .HasColumnName("JOB_KEYNAME")
            .HasMaxLength(50)
            .IsRequired();
        entity.Property(config => config.JobDesc)
            .HasColumnName("JOB_DESC")
            .HasMaxLength(100);
        entity.Property(config => config.TriggerKeyName)
            .HasColumnName("TRIGGER_KEYNAME")
            .HasMaxLength(50)
            .IsRequired();
        entity.Property(config => config.TriggerDesc)
            .HasColumnName("TRIGGER_DESC")
            .HasMaxLength(100);
        entity.Property(config => config.Cron)
            .HasColumnName("CRON")
            .HasMaxLength(50)
            .IsRequired();
        entity.Property(config => config.CronDesc)
            .HasColumnName("CRON_DESC")
            .HasMaxLength(100);
        entity.Property(config => config.IsEnabled)
            .HasColumnName("IS_ENABLE")
            .HasMaxLength(1)
            .IsRequired()
            .HasConversion(new ValueConverter<bool, string>(
                enabled => enabled ? "Y" : "N",
                value => string.Equals(value, "Y", StringComparison.OrdinalIgnoreCase)));
    }
}
