using System.Runtime.CompilerServices;

namespace MemoryCache;

/// <summary>
/// 规范化的复合缓存键。值会被扁平化，因此调用方产生的
/// <see cref="ITuple"/>（例如 ValueTuple）无论其具体泛型参数如何，
/// 都被视为相等。
/// </summary>
internal sealed class CacheKey : IEquatable<CacheKey>
{
    private readonly object?[] _values;
    private readonly int _hashCode;

    internal CacheKey(object?[] values)
    {
        if (values.Length == 0)
        {
            throw new ArgumentException("A composite cache key must contain at least one value.", nameof(values));
        }

        _values = values;
        _hashCode = ComputeHashCode(values);
    }

    internal static CacheKey FromValues(object?[] values)
        => new(values);

    /// <summary>
    /// 将任意的用户键转换为规范化的字典键。元组（包括嵌套元组及其他
    /// <see cref="CacheKey"/> 实例）会被扁平化为单层复合键。
    /// </summary>
    internal static object Normalize(object? key)
    {
        if (key is CacheKey or ITuple)
        {
            var parts = new List<object?>();
            Flatten(key, parts);
            return new CacheKey(parts.ToArray());
        }

        return key!;
    }

    public bool Equals(CacheKey? other)
    {
        if (other is null || _values.Length != other._values.Length)
        {
            return false;
        }

        for (var i = 0; i < _values.Length; i++)
        {
            if (!Equals(_values[i], other._values[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj)
        => obj is CacheKey other && Equals(other);

    public override int GetHashCode()
        => _hashCode;

    internal static IEnumerable<object?> GetParts(object? key)
    {
        var parts = new List<object?>();
        Flatten(key, parts);
        return parts;
    }

    private static void Flatten(object? value, List<object?> result)
    {
        switch (value)
        {
            case CacheKey cacheKey:
                foreach (var part in cacheKey._values)
                {
                    Flatten(part, result);
                }

                break;
            case ITuple tuple:
                for (var i = 0; i < tuple.Length; i++)
                {
                    Flatten(tuple[i], result);
                }

                break;
            default:
                result.Add(value);
                break;
        }
    }

    private static int ComputeHashCode(object?[] values)
    {
        var hash = new HashCode();
        foreach (var value in values)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }
}
