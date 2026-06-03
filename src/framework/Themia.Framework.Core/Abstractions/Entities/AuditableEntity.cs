namespace Themia.Framework.Core.Abstractions.Entities;

/// <summary>
/// Provides auditing metadata for entities.
/// </summary>
public interface IAuditableEntity
{
    /// <summary>
    /// Gets the timestamp when the entity was created.
    /// </summary>
    DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// Gets the identifier of the creator, if available.
    /// </summary>
    string? CreatedBy { get; }

    /// <summary>
    /// Gets the timestamp when the entity was last modified.
    /// </summary>
    DateTimeOffset? LastModifiedAt { get; }

    /// <summary>
    /// Gets the identifier of the last modifier, if available.
    /// </summary>
    string? LastModifiedBy { get; }
}

/// <summary>
/// Entity base type that tracks creation and modification metadata.
/// </summary>
/// <typeparam name="TId">Identifier type.</typeparam>
public abstract class AuditableEntity<TId> : Entity<TId>, IAuditableEntity
{
    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public string? CreatedBy { get; set; }

    /// <inheritdoc />
    public DateTimeOffset? LastModifiedAt { get; set; }

    /// <inheritdoc />
    public string? LastModifiedBy { get; set; }

    /// <summary>
    /// Marks the entity as created with metadata.
    /// </summary>
    /// <param name="timestamp">Creation timestamp.</param>
    /// <param name="createdBy">Creator identifier.</param>
    protected void MarkCreated(DateTimeOffset timestamp, string? createdBy = null)
    {
        CreatedAt = timestamp;
        CreatedBy = createdBy;
    }

    /// <summary>
    /// Marks the entity as modified with metadata.
    /// </summary>
    /// <param name="timestamp">Modification timestamp.</param>
    /// <param name="modifiedBy">Modifier identifier.</param>
    protected void MarkModified(DateTimeOffset timestamp, string? modifiedBy = null)
    {
        LastModifiedAt = timestamp;
        LastModifiedBy = modifiedBy;
    }
}

/// <summary>
/// Entity base type that tracks creation, modification metadata, and supports optimistic concurrency.
/// </summary>
/// <typeparam name="TId">Identifier type.</typeparam>
public abstract class ConcurrencyAwareEntity<TId> : AuditableEntity<TId>, IConcurrencyAware
{
    /// <inheritdoc />
    public byte[]? RowVersion { get; set; }
}
