using System.Linq.Expressions;
using System.Reflection;

namespace MemoryCache;

/// <summary>
/// 从实体条目中提取缓存键。
/// </summary>
/// <typeparam name="TEntity">被缓存的实体类型。</typeparam>
internal sealed class CacheKeyAccessor<TEntity>
{
    public required Func<TEntity, object?> Select { get; init; }

    public bool IsComposite { get; init; }
}

internal static class CacheKeyAccessorFactory
{
    public static CacheKeyAccessor<TEntity>? Create<TEntity>(
        string? propertyName,
        Func<TEntity, object?>? keySelector)
        where TEntity : class
    {
        if (keySelector is not null)
        {
            return new CacheKeyAccessor<TEntity>
            {
                Select = keySelector,
                IsComposite = false,
            };
        }

        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return null;
        }

        var properties = propertyName
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (properties.Length == 1)
        {
            var getter = CreatePropertyGetter<TEntity>(properties[0]);
            return new CacheKeyAccessor<TEntity>
            {
                Select = getter,
                IsComposite = false,
            };
        }

        return new CacheKeyAccessor<TEntity>
        {
            Select = CreateCompositeGetter<TEntity>(properties),
            IsComposite = true,
        };
    }

    private static Func<TEntity, object?> CreatePropertyGetter<TEntity>(string propertyName)
    {
        var entity = Expression.Parameter(typeof(TEntity), "entity");
        var property = Expression.Property(entity, propertyName);
        var body = Expression.Convert(property, typeof(object));
        return Expression.Lambda<Func<TEntity, object?>>(body, entity).Compile();
    }

    private static Func<TEntity, object?> CreateCompositeGetter<TEntity>(IReadOnlyList<string> propertyNames)
    {
        var entity = Expression.Parameter(typeof(TEntity), "entity");
        var values = new Expression[propertyNames.Count];

        for (var i = 0; i < propertyNames.Count; i++)
        {
            var property = GetProperty(typeof(TEntity), propertyNames[i]);
            values[i] = Expression.Convert(Expression.Property(entity, property), typeof(object));
        }

        var array = Expression.NewArrayInit(typeof(object), values);
        var constructor = typeof(CacheKey).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(object[])],
            modifiers: null);

        if (constructor is null)
        {
            throw new MissingMethodException(typeof(CacheKey).FullName, ".ctor(object[])");
        }

        var body = Expression.New(constructor, array);
        return Expression.Lambda<Func<TEntity, object?>>(body, entity).Compile();
    }

    private static PropertyInfo GetProperty(Type entityType, string propertyName)
    {
        return entityType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"Type '{entityType.Name}' has no public instance property named '{propertyName}'.");
    }
}
