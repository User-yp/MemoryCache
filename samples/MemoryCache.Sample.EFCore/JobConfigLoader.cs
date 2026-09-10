using MemoryCache.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace MemoryCache.Sample.EFCore;

/// <summary>
/// 通过 EF Core（<see cref="IDbContextFactory{TContext}"/> + AsNoTracking）
/// 全量加载 <c>job_config</c> 表。
/// </summary>
public sealed class JobConfigLoader(
    IDbContextFactory<JobConfigDbContext> dbContextFactory)
    : IEntityLoader<JobConfig>
{
    private int _loadCount;

    public int LoadCount => Volatile.Read(ref _loadCount);

    public async Task<IReadOnlyCollection<JobConfig>> LoadAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _loadCount);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.JobConfigs
            .AsNoTracking()
            .OrderBy(config => config.Group)
            .ThenBy(config => config.JobKeyName)
            .ToListAsync(cancellationToken);
    }
}
