namespace MemoryCache;

/// <summary>
/// 单个实体缓存项的不可变快照，通过原子发布对外可见。
/// </summary>
/// <typeparam name="TEntity">被缓存的实体类型。</typeparam>
internal sealed class CacheSnapshot<TEntity>
{
    /// <summary>
    /// 条目列表。以只读包装对外暴露，避免调用方强转成数组后修改缓存内容。
    /// </summary>
    public required IReadOnlyList<TEntity> Items { get; init; }

    /// <summary>
    /// 键索引；实体未配置缓存键时为 <c>null</c>。
    /// </summary>
    public IReadOnlyDictionary<object, TEntity>? Index { get; init; }

    public required long Version { get; init; }

    public required DateTimeOffset LoadedAt { get; init; }
}
