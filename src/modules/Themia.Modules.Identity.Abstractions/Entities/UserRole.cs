namespace Themia.Modules.Identity.Abstractions.Entities;

/// <summary>Join row assigning a <see cref="Role"/> to a <see cref="User"/>. Carries a surrogate id so it keys uniformly through the generic repository; a unique index on (user_id, role_id) prevents duplicates.</summary>
public sealed class UserRole
{
    /// <summary>The surrogate identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>The user identifier.</summary>
    public Guid UserId { get; set; }

    /// <summary>The role identifier.</summary>
    public Guid RoleId { get; set; }

    /// <summary>Assigns the identifier for a new (transient) membership.</summary>
    /// <param name="id">A client-generated identifier.</param>
    public void SetId(Guid id) => Id = id;
}
