namespace MemoryCache;

internal sealed class EntityCacheOptions
{
    public required IReadOnlyList<EntityCacheRegistration> Registrations { get; init; }

    public required EntityCacheServiceOptions ServiceOptions { get; init; }
}
