namespace Themia.Framework.Core.Abstractions.Events;

/// <summary>
/// Marker interface for domain events that represent significant occurrences in the domain.
/// </summary>
public interface IDomainEvent
{
    /// <summary>
    /// Gets the timestamp when the event occurred.
    /// </summary>
    DateTimeOffset OccurredAt { get; }
}

/// <summary>
/// Base record for domain events with automatic timestamp tracking.
/// </summary>
public abstract record DomainEventBase : IDomainEvent
{
    /// <summary>
    /// Initializes a new domain event with the current UTC timestamp.
    /// </summary>
    protected DomainEventBase()
    {
        OccurredAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Initializes a new domain event with a specific timestamp.
    /// </summary>
    /// <param name="occurredAt">Timestamp when the event occurred.</param>
    protected DomainEventBase(DateTimeOffset occurredAt)
    {
        OccurredAt = occurredAt;
    }

    /// <inheritdoc />
    public DateTimeOffset OccurredAt { get; init; }
}
