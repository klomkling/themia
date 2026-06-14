using System.Security.Claims;

namespace Themia.Modules.Identity.Abstractions;

/// <summary>The ambient authenticated principal for the current request. Inject this into application code.</summary>
public interface ICurrentUser
{
    /// <summary>Whether a user is authenticated.</summary>
    bool IsAuthenticated { get; }

    /// <summary>The authenticated user's id, or null when unauthenticated.</summary>
    Guid? UserId { get; }

    /// <summary>The user's tenant id, or null for a platform (cross-tenant) user or when unauthenticated.</summary>
    string? TenantId { get; }

    /// <summary>Whether the user is a platform (cross-tenant) user — true when authenticated with no tenant.</summary>
    bool IsPlatform { get; }

    /// <summary>The user's login name, or null when unauthenticated.</summary>
    string? UserName { get; }

    /// <summary>The user's role names.</summary>
    IReadOnlyCollection<string> Roles { get; }

    /// <summary>The user's claims.</summary>
    IReadOnlyCollection<Claim> Claims { get; }

    /// <summary>Whether the user is in the named role.</summary>
    /// <param name="role">The role name.</param>
    /// <returns><see langword="true"/> when the user holds the role.</returns>
    bool IsInRole(string role);
}
