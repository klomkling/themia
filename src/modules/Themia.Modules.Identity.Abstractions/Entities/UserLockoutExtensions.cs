namespace Themia.Modules.Identity.Abstractions.Entities;

/// <summary>Shared lockout predicate over <see cref="User"/>, so every authentication path (password
/// verification, external login, auto-link) evaluates lockout identically and a change to the rule lands
/// in one place.</summary>
public static class UserLockoutExtensions
{
    /// <summary>Whether the account is currently locked out at <paramref name="now"/>: lockout is enabled
    /// and the lockout window has not yet elapsed.</summary>
    /// <param name="user">The user.</param>
    /// <param name="now">The current time (from the caller's <see cref="System.TimeProvider"/>).</param>
    /// <returns><see langword="true"/> when the account is locked out.</returns>
    public static bool IsLockedOut(this User user, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.LockoutEnabled && user.LockoutEnd is { } end && end > now;
    }
}
