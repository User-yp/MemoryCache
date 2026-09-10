using System.Runtime.CompilerServices;

namespace MemoryCache;

/// <summary>
/// 用于复合缓存键的结构化比较器。基于元组的用户键会被扁平化并逐分量比较，
/// 因此 <c>(string, string)</c> 与内部规范化键是等价的。
/// </summary>
internal sealed class CacheKeyComparer : IEqualityComparer<object>
{
    internal static readonly CacheKeyComparer Instance = new();

    public new bool Equals(object? x, object? y)
    {
        if (ReferenceEquals(x, y))
        {
            return true;
        }

        if (x is null || y is null)
        {
            return false;
        }

        var xIsStructural = x is CacheKey or ITuple;
        var yIsStructural = y is CacheKey or ITuple;

        if (xIsStructural || yIsStructural)
        {
            if (!xIsStructural || !yIsStructural)
            {
                return false;
            }

            var xParts = CacheKey.GetParts(x).ToArray();
            var yParts = CacheKey.GetParts(y).ToArray();
            if (xParts.Length != yParts.Length)
            {
                return false;
            }

            for (var i = 0; i < xParts.Length; i++)
            {
                if (!Equals(xParts[i], yParts[i]))
                {
                    return false;
                }
            }

            return true;
        }

        return EqualityComparer<object>.Default.Equals(x, y);
    }

    public int GetHashCode(object obj)
    {
        var hash = new HashCode();
        foreach (var part in CacheKey.GetParts(obj))
        {
            hash.Add(part);
        }

        return hash.ToHashCode();
    }
}
