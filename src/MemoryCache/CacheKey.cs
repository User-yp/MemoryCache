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
        HasNestedValues = ContainsNestedValues(values);
    }

    /// <summary>
    /// 获取一个值，指示键的分量本身是否还是键（嵌套元组/嵌套规范化键）。
    /// 这类键需要先展开再比较，不能按下标直接比。
    /// </summary>
    internal bool HasNestedValues { get; }

    internal int Length
        => _values.Length;

    internal object? GetValue(int index)
        => _values[index];

    /// <summary>
    /// 判断一个键是否属于“可展开”的结构化键（元组或规范化键）。
    /// </summary>
    internal static bool IsStructural(object? key)
        => key is CacheKey or ITuple;

    /// <summary>
    /// 判断元组的各个分量是否都是标量；含嵌套键的元组需要展开后比较。
    /// </summary>
    internal static bool IsFlatTuple(ITuple tuple)
    {
        for (var i = 0; i < tuple.Length; i++)
        {
            if (IsStructural(tuple[i]))
            {
                return false;
            }
        }

        return true;
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

    private static bool ContainsNestedValues(object?[] values)
    {
        foreach (var value in values)
        {
            if (IsStructural(value))
            {
                return true;
            }
        }

        return false;
    }
}
