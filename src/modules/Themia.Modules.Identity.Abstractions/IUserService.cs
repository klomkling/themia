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

    /// <summary>Creates an active, password-less user from an external identity. The username is
    /// caller-supplied (already derived + unique); <paramref name="emailVerified"/> sets EmailConfirmed.</summary>
    /// <param name="userName">The derived, unique login name.</param>
    /// <param name="email">An optional email address.</param>
    /// <param name="emailVerified">Whether the provider asserts the email is verified; sets EmailConfirmed.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A <see cref="UserCreationResult"/>.</returns>
    Task<UserCreationResult> CreateExternalUserAsync(
        string userName, string? email, bool emailVerified, CancellationToken cancellationToken = default);

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

    /// <summary>Finds a user by phone number — first in the ambient tenant, then (when allowed) in the platform scope.</summary>
    /// <remarks>
    /// Matches on the normalized form, so the caller may pass the number in any accepted formatting.
    /// Returns a user whose phone is <b>not</b> confirmed as readily as one whose phone is — confirmation
    /// is an authorization question, and the caller that cares (the login flow) applies it. A lookup that
    /// silently hid unconfirmed rows would make "no such number" and "not confirmed yet" the same answer
    /// to code trying to tell them apart.
    /// </remarks>
    /// <param name="phoneNumber">The phone number, in any accepted formatting.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The user, or null.</returns>
    Task<User?> FindByPhoneAsync(string phoneNumber, CancellationToken cancellationToken = default);

    /// <summary>Sets (or replaces) a user's phone number, storing its normalized form and clearing
    /// <see cref="User.PhoneNumberConfirmed"/>.</summary>
    /// <remarks>
    /// Confirmation is always cleared, including when the number is unchanged: a phone is confirmed by
    /// proving control of it, and that proof belongs to one number at one time. Leaving the flag set
    /// across a change would let anyone who can edit a profile inherit someone else's confirmed status
    /// and, once phone login is enabled, log in as them. Confirm again through
    /// <see cref="ConfirmPhoneNumberAsync"/>.
    /// </remarks>
    /// <param name="userId">The user id.</param>
    /// <param name="phoneNumber">The phone number as entered, or <see langword="null"/> to remove it.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The outcome — see <see cref="SetPhoneNumberResult"/>.</returns>
    Task<SetPhoneNumberResult> SetPhoneNumberAsync(Guid userId, string? phoneNumber, CancellationToken cancellationToken = default);

    /// <summary>Marks a user's phone number as confirmed.</summary>
    /// <remarks>
    /// <b>Themia does not verify the number for you and this method asserts nothing about it.</b> Call it
    /// only after your own proof of control has succeeded — a one-time code delivered to that number, for
    /// which <c>Themia.Challenges</c> exists. Identity deliberately takes no dependency on it, so nothing
    /// here can check that you did.
    /// </remarks>
    /// <param name="userId">The user id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><see langword="true"/> when the user was found and has a phone number to confirm.</returns>
    Task<bool> ConfirmPhoneNumberAsync(Guid userId, CancellationToken cancellationToken = default);

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
