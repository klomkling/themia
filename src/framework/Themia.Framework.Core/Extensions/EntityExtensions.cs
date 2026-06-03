using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Events;

namespace Themia.Framework.Core.Extensions;

/// <summary>
/// Extension methods for Entity operations using modern C# 14 features.
/// </summary>
public static class EntityExtensions
{
    /// <summary>
    /// Dispatches all domain events from an entity and clears them.
    /// Uses C# 14 collection expressions and params collections.
    /// </summary>
    /// <typeparam name="TId">Entity identifier type.</typeparam>
    /// <param name="entity">The entity.</param>
    /// <param name="dispatcher">Event dispatcher.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    public static async Task DispatchAndClearEventsAsync<TId>(
        this Entity<TId> entity,
        IDomainEventDispatcher dispatcher,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(dispatcher);

        if (entity.DomainEvents.Count == 0)
        {
            return;
        }

        var domainEvents = entity.DomainEvents.ToArray(); // snapshot to avoid mutation during dispatch
        await dispatcher.DispatchAsync(domainEvents, cancellationToken);
        entity.ClearDomainEvents();
    }

    /// <summary>
    /// Dispatches and clears events from multiple entities.
    /// Demonstrates params collections - a C# 14 feature.
    /// </summary>
    /// <typeparam name="TId">Entity identifier type.</typeparam>
    /// <param name="dispatcher">Event dispatcher.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="entities">Entities to process.</param>
    /// <returns>Task representing the async operation.</returns>
    public static async Task DispatchAndClearEventsAsync<TId>(
        this IDomainEventDispatcher dispatcher,
        CancellationToken cancellationToken = default,
        params IEnumerable<Entity<TId>> entities)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(entities);

        List<IDomainEvent> allEvents = [];

        foreach (var entity in entities)
        {
            allEvents.AddRange(entity.DomainEvents.ToArray()); // snapshot per entity to avoid collection mutation issues
        }

        if (allEvents.Count > 0)
        {
            await dispatcher.DispatchAsync(allEvents, cancellationToken);

            foreach (var entity in entities)
            {
                entity.ClearDomainEvents();
            }
        }
    }

    /// <summary>
    /// Gets all entities that have pending domain events.
    /// Uses C# 14 collection expressions and LINQ optimizations.
    /// </summary>
    /// <typeparam name="TId">Entity identifier type.</typeparam>
    /// <param name="entities">Entities to filter.</param>
    /// <returns>Entities with pending events.</returns>
    public static IEnumerable<Entity<TId>> WithPendingEvents<TId>(
        this IEnumerable<Entity<TId>> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);

        return entities.Where(e => e.DomainEvents.Count > 0);
    }

    /// <summary>
    /// Checks if an entity is persisted (not transient).
    /// </summary>
    /// <typeparam name="TId">Entity identifier type.</typeparam>
    /// <param name="entity">The entity.</param>
    /// <returns>True if entity is persisted.</returns>
    public static bool IsPersisted<TId>(this Entity<TId> entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return !entity.IsTransient;
    }

    /// <summary>
    /// Groups entities by their transient/persisted state.
    /// Uses C# 14 tuple deconstruction and collection expressions.
    /// </summary>
    /// <typeparam name="TId">Entity identifier type.</typeparam>
    /// <param name="entities">Entities to partition.</param>
    /// <returns>Tuple of transient and persisted entities.</returns>
    public static (IReadOnlyList<Entity<TId>> Transient, IReadOnlyList<Entity<TId>> Persisted)
        PartitionByState<TId>(this IEnumerable<Entity<TId>> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);

        List<Entity<TId>> transient = [];
        List<Entity<TId>> persisted = [];

        foreach (var entity in entities)
        {
            if (entity.IsTransient)
            {
                transient.Add(entity);
            }
            else
            {
                persisted.Add(entity);
            }
        }

        return (transient, persisted);
    }

    /// <summary>
    /// Safely adds entities to a collection, avoiding hash-based collection issues.
    /// Assigns IDs first if they're Guid-based and not set.
    /// </summary>
    /// <param name="collection">Target collection.</param>
    /// <param name="entities">Entities to add.</param>
    /// <returns>The collection for chaining.</returns>
    public static ICollection<Entity<Guid>> AddSafely(
        this ICollection<Entity<Guid>> collection,
        params IEnumerable<Entity<Guid>> entities)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(entities);

        foreach (var entity in entities)
        {
            // Ensure ID is set before adding to avoid hash code instability
            if (entity.IsTransient)
            {
                // For Guid-based entities, generate ID if not set
                // This prevents issues with HashSet/Dictionary
                var idProperty = entity.GetType().GetProperty(nameof(Entity<Guid>.Id));
                if (idProperty?.CanWrite == true)
                {
                    idProperty.SetValue(entity, Guid.NewGuid());
                }
            }

            collection.Add(entity);
        }

        return collection;
    }
}
