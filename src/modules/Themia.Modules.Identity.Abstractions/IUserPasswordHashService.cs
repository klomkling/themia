namespace Themia.Modules.Identity.Abstractions;

/// <summary>
/// Writes a password hash the caller has already computed. For an app that keeps its own hash and mirrors it into
/// Themia, where <see cref="IUserService.SetPasswordAsync"/> would hash the plaintext again and the two hashes would
/// differ. Resolve it from the same scope as <see cref="IUserService"/>; it is the same instance.
/// </summary>
public interface IUserPasswordHashService
{
    /// <summary>
    /// Stores <paramref name="passwordHash"/> as given, rotates the security stamp, voids the user's live
    /// password-reset links, lifts any lock and announces <see cref="UserMutation.Password"/> — exactly what
    /// <see cref="IUserService.SetPasswordAsync"/> does after it has hashed.
    /// </summary>
    /// <remarks>
    /// <para><b>In the caller's transaction.</b> The write joins the scope's ambient transaction. The stamp-cache
    /// eviction and the mutation announcement happen when this call returns, before your commit, so call it as the
    /// last statement before committing: a request on the old stamp can be cached for up to the stamp window after
    /// the commit, as on other instances, and observers see a change that a later rollback would undo.</para>
    /// <para><b>Have no other pending changes on the unit of work when you call it: save your own writes first.</b>
    /// A save that loses a race to another writer discards the pending changes of the whole unit of work, not only
    /// the user row, and the retry then reports success. Your staged write would be gone while the call reports
    /// success. For a platform user (no tenant) the save also skips tenant write-validation for everything pending.</para>
    /// <para><b>Any result other than success: roll your transaction back.</b> After <c>Conflict</c> the reset links
    /// are already voided (the void runs before the save), so committing leaves your store with the new hash,
    /// Themia with the old one and the links spent. <c>Refused</c> and <c>UserNotFound</c> change nothing, but treat
    /// them the same way: the two stores now disagree. Throw on a non-success result.</para>
    /// <para><b>Themia never verifies a hash its <see cref="IPasswordHasher"/> cannot parse</b> (for example bcrypt
    /// under the default Argon2id hasher). Logging in through <see cref="IUserService.VerifyPasswordAsync"/> with
    /// such a hash is a wrong guess that counts towards the lockout, so keep login in your own store unless the
    /// hasher understands the hash.</para>
    /// </remarks>
    /// <param name="userId">The user id.</param>
    /// <param name="passwordHash">The encoded hash, or <see langword="null"/> to clear the password (the links are
    /// voided then too).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The outcome — see <see cref="UserMutationResult"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="passwordHash"/> is empty, whitespace or longer than 1024
    /// characters. <see langword="null"/> is the only way to clear.</exception>
    Task<UserMutationResult> SetPasswordHashAsync(Guid userId, string? passwordHash, CancellationToken cancellationToken = default);
}
