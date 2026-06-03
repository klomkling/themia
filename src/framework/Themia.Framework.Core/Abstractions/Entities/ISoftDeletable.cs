namespace Themia.Framework.Core.Abstractions.Entities;

/// <summary>
/// Marks an entity as supporting soft deletion.
/// </summary>
public interface ISoftDeletable
{
    /// <summary>
    /// Gets a value indicating whether the entity is deleted.
    /// </summary>
    bool IsDeleted { get; }

    /// <summary>
    /// Gets the timestamp when the entity was deleted.
    /// </summary>
    DateTimeOffset? DeletedAt { get; }

    /// <summary>
    /// Gets the identifier of the user who deleted the entity.
    /// </summary>
    string? DeletedBy { get; }

    /// <summary>
    /// Gets the timestamp when the entity was restored.
    /// </summary>
    DateTimeOffset? RestoredAt { get; }

    /// <summary>
    /// Gets the identifier of the user who restored the entity.
    /// </summary>
    string? RestoredBy { get; }
}

/// <summary>
/// Base entity type with soft delete support.
/// </summary>
/// <typeparam name="TId">Identifier type.</typeparam>
public abstract class SoftDeletableEntity<TId> : AuditableEntity<TId>, ISoftDeletable
{
    /// <inheritdoc />
    public bool IsDeleted { get; set; }

    /// <inheritdoc />
    public DateTimeOffset? DeletedAt { get; set; }

    /// <inheritdoc />
    public string? DeletedBy { get; set; }

    /// <inheritdoc />
    public DateTimeOffset? RestoredAt { get; set; }

    /// <inheritdoc />
    public string? RestoredBy { get; set; }

    /// <summary>
    /// Marks the entity as deleted.
    /// </summary>
    /// <param name="timestamp">Deletion timestamp.</param>
    /// <param name="deletedBy">Identifier of user performing deletion.</param>
    protected void MarkDeleted(DateTimeOffset timestamp, string? deletedBy = null)
    {
        IsDeleted = true;
        DeletedAt = timestamp;
        DeletedBy = deletedBy;
    }

    /// <summary>
    /// Restores a soft-deleted entity.
    /// </summary>
    /// <param name="timestamp">Restoration timestamp.</param>
    /// <param name="restoredBy">Identifier of user performing restoration.</param>
    protected void Restore(DateTimeOffset timestamp, string? restoredBy = null)
    {
        IsDeleted = false;
        RestoredAt = timestamp;
        RestoredBy = restoredBy;
    }
}
