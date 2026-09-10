using System.Runtime.CompilerServices;

namespace MemoryCache;

/// <summary>
/// 用于复合缓存键的结构化比较器。基于元组的用户键会被扁平化并逐分量比较，
/// 因此 <c>(string, string)</c> 与内部规范化键是等价的。
/// </summary>
/// <remarks>
/// 键中不含嵌套元组时走零分配路径：规范化键与元组都按下标直接访问，
/// 不再构造 <see cref="List{T}"/> 或数组（规范化键还能复用构造时缓存的哈希）。
/// 只有出现嵌套键时才回退到展开式比较，语义与展开后完全一致。
/// </remarks>
internal sealed class CacheKeyComparer : IEqualityComparer<object>
{
    internal static readonly CacheKeyComparer Instance = new();

    public new bool Equals(object? x, object? y)
        => EqualsCore(x, y);

    public int GetHashCode(object obj)
    {
        if (!CacheKey.IsStructural(obj))
        {
            return obj.GetHashCode();
        }

        if (!TryGetFlatLength(obj, out var length))
        {
            // 嵌套键：按展开后的分量计算哈希，与展开式比较保持一致。
            var nestedHash = new HashCode();
            foreach (var part in CacheKey.GetParts(obj))
            {
                nestedHash.Add(part);
            }

            return nestedHash.ToHashCode();
        }

        // 规范化键在构造时已缓存哈希，直接复用。
        if (obj is CacheKey cacheKey)
        {
            return cacheKey.GetHashCode();
        }

        var hash = new HashCode();
        for (var i = 0; i < length; i++)
        {
            hash.Add(GetPart(obj, i));
        }

        return hash.ToHashCode();
    }

    private static bool EqualsCore(object? x, object? y)
    {
        if (ReferenceEquals(x, y))
        {
            return true;
        }

        if (x is null || y is null)
        {
            return false;
        }

        var xStructural = CacheKey.IsStructural(x);
        var yStructural = CacheKey.IsStructural(y);

        if (!xStructural || !yStructural)
        {
            return !xStructural && !yStructural && EqualityComparer<object>.Default.Equals(x, y);
        }

        if (TryGetFlatLength(x, out var xLength) && TryGetFlatLength(y, out var yLength))
        {
            if (xLength != yLength)
            {
                return false;
            }

            for (var i = 0; i < xLength; i++)
            {
                if (!EqualsCore(GetPart(x, i), GetPart(y, i)))
                {
                    return false;
                }
            }

            return true;
        }

        // 含嵌套键：回退到展开比较。
        var xParts = CacheKey.GetParts(x).ToArray();
        var yParts = CacheKey.GetParts(y).ToArray();
        if (xParts.Length != yParts.Length)
        {
            return false;
        }

        for (var i = 0; i < xParts.Length; i++)
        {
            if (!EqualsCore(xParts[i], yParts[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetFlatLength(object structuralKey, out int length)
    {
        switch (structuralKey)
        {
            case CacheKey cacheKey when !cacheKey.HasNestedValues:
                length = cacheKey.Length;
                return true;
            case ITuple tuple when CacheKey.IsFlatTuple(tuple):
                length = tuple.Length;
                return true;
            default:
                length = 0;
                return false;
        }
    }

    private static object? GetPart(object structuralKey, int index)
        => structuralKey is CacheKey cacheKey
            ? cacheKey.GetValue(index)
            : ((ITuple)structuralKey)[index];
}
