using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.Abstractions;

/// <summary>Creates and manages <see cref="User"/> accounts within the ambient tenant (and, for lookups, the platform scope).</summary>
public interface IUserService
{
    /// <summary>Creates a user in the ambient tenant with the given password. Normalizes the user name and email.</summary>
    /// <param name="userName">The login name.</param>
    /// <param name="password">The plaintext password (hashed before storage).</param>
    /// <param name="email">An optional email address.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A <see cref="UserCreationResult"/>.</returns>
    Task<UserCreationResult> CreateAsync(string userName, string password, string? email = null, CancellationToken cancellationToken = default);

    /// <summary>Finds a user by id within the ambient tenant.</summary>
    /// <param name="id">The user id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The user, or null.</returns>
    Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Finds a user by login name — first in the ambient tenant, then (when allowed) in the platform scope.</summary>
    /// <param name="userName">The login name (any casing).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The user, or null.</returns>
    Task<User?> FindByUserNameAsync(string userName, CancellationToken cancellationToken = default);

    /// <summary>Finds a user by email — first in the ambient tenant, then (when allowed) in the platform scope.</summary>
    /// <param name="email">The email address (any casing).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The user, or null.</returns>
    Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>Sets (or replaces) a user's password and reissues the security stamp.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="password">The new plaintext password.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><see langword="true"/> when the user was found and updated.</returns>
    Task<bool> SetPasswordAsync(Guid userId, string password, CancellationToken cancellationToken = default);

    /// <summary>Verifies a password and applies the lockout state machine (increments/locks on failure, resets on success).</summary>
    /// <param name="userName">The login name.</param>
    /// <param name="password">The plaintext password.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The <see cref="PasswordVerificationResult"/>.</returns>
    Task<PasswordVerificationResult> VerifyPasswordAsync(string userName, string password, CancellationToken cancellationToken = default);

    /// <summary>Enables or disables an account.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="isActive">Whether the account is enabled.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><see langword="true"/> when the user was found and updated.</returns>
    Task<bool> SetActiveAsync(Guid userId, bool isActive, CancellationToken cancellationToken = default);

    /// <summary>Soft-deletes a user.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><see langword="true"/> when the user was found and deleted.</returns>
    Task<bool> DeleteAsync(Guid userId, CancellationToken cancellationToken = default);
}
