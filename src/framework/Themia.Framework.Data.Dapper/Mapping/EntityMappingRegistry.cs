using System.Collections.Concurrent;

namespace Themia.Framework.Data.Dapper.Mapping;

/// <summary>
/// Caches one <see cref="EntityMapping"/> per entity type. Register overrides at startup.
/// </summary>
public sealed class EntityMappingRegistry
{
    private readonly ConcurrentDictionary<Type, EntityMapping> _cache = new();
    private readonly ConcurrentDictionary<Type, EntityMapping> _overrides = new();

    /// <summary>Registers an explicit mapping for <typeparamref name="T"/>, overriding the convention.</summary>
    public void Register<T>(EntityMapping mapping) => _overrides[typeof(T)] = mapping;

    /// <summary>Gets the mapping for <typeparamref name="T"/> (cached).</summary>
    public EntityMapping For<T>() => For(typeof(T));

    /// <summary>Gets the mapping for the given entity type (cached).</summary>
    public EntityMapping For(Type type) =>
        _cache.GetOrAdd(type, t => _overrides.TryGetValue(t, out var m) ? m : EntityMapping.ForConvention(t));
}
