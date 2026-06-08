namespace Themia.Framework.Data.Abstractions.Auditing;

/// <summary>Supplies the identifier stamped into CreatedBy/LastModifiedBy/DeletedBy. Default impls return null.</summary>
public interface ICurrentUserAccessor
{
    /// <summary>The current user identifier, or null when unknown/unauthenticated.</summary>
    string? UserId { get; }
}
