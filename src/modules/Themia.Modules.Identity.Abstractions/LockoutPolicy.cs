namespace Themia.Modules.Identity.Abstractions;

/// <summary>What happens to <see cref="Entities.User.AccessFailedCount"/> and the lock length when an account
/// reaches <see cref="IdentityModuleOptions.MaxFailedAccessAttempts"/>.</summary>
public enum LockoutPolicy
{
    /// <summary>The count is zeroed when the account locks, so each lock buys
    /// <see cref="IdentityModuleOptions.MaxFailedAccessAttempts"/> fresh guesses once it expires. The default.</summary>
    ResetCountOnLock = 0,

    /// <summary>The count is kept, so the first wrong password after a lock expires locks the account again
    /// for <see cref="IdentityModuleOptions.LockoutDuration"/>. A successful login clears the count.</summary>
    KeepCount = 1,

    /// <summary>As <see cref="KeepCount"/>, and each consecutive lock doubles the duration
    /// (<see cref="IdentityModuleOptions.LockoutDuration"/>, then twice that, ...) up to
    /// <see cref="IdentityModuleOptions.MaxLockoutDuration"/>. A successful login restarts the sequence.
    /// Lets an attacker who knows an account name hold it locked for up to the cap.</summary>
    Progressive = 2,
}
