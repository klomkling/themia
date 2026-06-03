using Themia.Framework.Core.Abstractions.Events;

namespace Themia.Framework.Core.Abstractions.Entities;

/// <summary>
/// Base type for entities with identity and domain events.
/// </summary>
/// <typeparam name="TId">Identifier type.</typeparam>
public abstract class Entity<TId> : IEquatable<Entity<TId>>
{
    private readonly List<IDomainEvent> domainEvents = [];

    /// <summary>
    /// Gets the entity identifier.
    /// </summary>
    public TId Id { get; protected set; } = default!;

    /// <summary>
    /// Gets domain events raised by the entity.
    /// </summary>
    public IReadOnlyCollection<IDomainEvent> DomainEvents => domainEvents;

    /// <summary>
    /// Indicates whether the entity has not been persisted yet.
    /// </summary>
    public bool IsTransient => EqualityComparer<TId>.Default.Equals(Id, default!);

    /// <summary>
    /// Adds a domain event to the entity.
    /// </summary>
    /// <param name="domainEvent">Domain event instance.</param>
    protected void AddDomainEvent(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        domainEvents.Add(domainEvent);
    }

    /// <summary>
    /// Clears domain events after dispatch.
    /// </summary>
    public void ClearDomainEvents() => domainEvents.Clear();

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Entity<TId> other && Equals(other);

    /// <inheritdoc />
    public bool Equals(Entity<TId>? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (IsTransient || other.IsTransient)
        {
            return false;
        }

        return EqualityComparer<TId>.Default.Equals(Id, other.Id);
    }

    /// <inheritdoc />
    /// <remarks>
    /// WARNING: Hash code behavior changes when entity transitions from transient to persistent state.
    /// Transient entities (Id not set) use reference-based hashing. Once Id is assigned, hash code
    /// switches to Id-based hashing. This can cause issues with hash-based collections (HashSet, Dictionary).
    ///
    /// Best practices:
    /// - Avoid adding transient entities to hash-based collections before persistence
    /// - If you must use collections, consider using List instead of HashSet for transient entities
    /// - Alternatively, assign IDs before adding to collections (e.g., use client-generated GUIDs)
    /// </remarks>
    public override int GetHashCode()
    {
        var id = Id;

        if (EqualityComparer<TId>.Default.Equals(id, default!))
        {
            return base.GetHashCode();
        }

        return EqualityComparer<TId>.Default.GetHashCode(id!);
    }

    /// <summary>
    /// Compares two entities for equality.
    /// </summary>
    public static bool operator ==(Entity<TId>? left, Entity<TId>? right) =>
        Equals(left, right);

    /// <summary>
    /// Compares two entities for inequality.
    /// </summary>
    public static bool operator !=(Entity<TId>? left, Entity<TId>? right) =>
        !Equals(left, right);
}
