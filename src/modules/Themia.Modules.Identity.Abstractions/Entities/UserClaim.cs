namespace Themia.Modules.Identity.Abstractions.Entities;

/// <summary>A claim granted directly to a user.</summary>
public sealed class UserClaim
{
    /// <summary>The claim identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning user identifier.</summary>
    public Guid UserId { get; set; }

    /// <summary>The claim type (e.g. a permission name).</summary>
    public string ClaimType { get; set; } = string.Empty;

    /// <summary>The claim value.</summary>
    public string ClaimValue { get; set; } = string.Empty;

    /// <summary>Assigns the identifier for a new (transient) claim.</summary>
    /// <param name="id">A client-generated identifier.</param>
    public void SetId(Guid id) => Id = id;
}
