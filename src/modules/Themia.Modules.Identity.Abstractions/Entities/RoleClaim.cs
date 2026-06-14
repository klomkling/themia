namespace Themia.Modules.Identity.Abstractions.Entities;

/// <summary>A claim granted to all users in a role.</summary>
public sealed class RoleClaim
{
    /// <summary>The claim identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning role identifier.</summary>
    public Guid RoleId { get; set; }

    /// <summary>The claim type.</summary>
    public string ClaimType { get; set; } = string.Empty;

    /// <summary>The claim value.</summary>
    public string ClaimValue { get; set; } = string.Empty;

    /// <summary>Assigns the identifier for a new (transient) claim.</summary>
    /// <param name="id">A client-generated identifier.</param>
    public void SetId(Guid id) => Id = id;
}
