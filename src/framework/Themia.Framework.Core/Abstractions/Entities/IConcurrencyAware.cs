namespace Themia.Framework.Core.Abstractions.Entities;

/// <summary>
/// Marks an entity as supporting optimistic concurrency control.
/// </summary>
/// <remarks>
/// Entities implementing this interface should have their RowVersion property
/// configured as a concurrency token in EF Core using <c>IsRowVersion()</c> or <c>IsConcurrencyToken()</c>.
/// </remarks>
public interface IConcurrencyAware
{
    /// <summary>
    /// Gets or sets the row version for optimistic concurrency control.
    /// </summary>
    /// <remarks>
    /// This value is automatically managed by the database and should not be modified manually.
    /// EF Core will use this to detect concurrent updates and throw DbUpdateConcurrencyException when conflicts occur.
    /// </remarks>
    byte[]? RowVersion { get; set; }
}
