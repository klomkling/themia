namespace Themia.Modules.Identity.Abstractions;

/// <summary>Tunable policy for the Identity module.</summary>
public sealed class IdentityModuleOptions
{
    private static readonly TimeSpan LongestAllowedMaxLockoutDuration = TimeSpan.FromDays(365);

    /// <summary>The configuration connection-string name the schema migration runs against. Defaults to <c>"Default"</c>.</summary>
    public string ConnectionStringName { get; set; } = "Default";

    /// <summary>Consecutive failed password attempts before lockout engages. Defaults to 5.</summary>
    public int MaxFailedAccessAttempts { get; set; } = 5;

    /// <summary>How long an account stays locked out once the threshold is hit. Defaults to 15 minutes.</summary>
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How the failed-attempt count and lock length behave when the threshold is hit.
    /// Defaults to <see cref="LockoutPolicy.ResetCountOnLock"/>.</summary>
    public LockoutPolicy LockoutPolicy { get; set; } = LockoutPolicy.ResetCountOnLock;

    /// <summary>The longest a single lock may last under <see cref="LockoutPolicy.Progressive"/>. Defaults to 24 hours.
    /// Ignored by the other policies.</summary>
    public TimeSpan MaxLockoutDuration { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Whether a platform (global) user may authenticate against a tenant entry point. Defaults to true.</summary>
    public bool AllowPlatformLogin { get; set; } = true;

    /// <summary>Default lifetime for generated <see cref="Entities.TokenPurpose"/> tokens. Defaults to 1 hour.</summary>
    public TimeSpan DefaultTokenLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>The lifetime of an issued refresh token. Default 14 days.</summary>
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(14);

    /// <summary>The <see cref="UserMutationResult.RefusalCode"/> of a phone number refused because an
    /// active user in another tenant already holds it (see <see cref="RejectPhoneNumberHeldInAnotherTenant"/>).</summary>
    public const string PhoneNumberInAnotherTenantRefusalCode = "phone_number_in_another_tenant";

    /// <summary>
    /// When <see langword="true"/>, <c>IUserService.SetPhoneNumberAsync</c> refuses a number that an active user
    /// in another tenant (or a platform user) already holds, with <see cref="PhoneNumberInAnotherTenantRefusalCode"/>.
    /// Off by default: tenants may legitimately share a number. Turn it on when you log in by one-time code with
    /// no tenant hint, where a number active in two tenants cannot be told apart and gets no code at all.
    /// Only blocks new assignments; numbers already shared are not touched. The check reads then writes, so two
    /// tenants setting the same number at the same instant can both pass; no portable constraint can say "unique
    /// across tenants among active users". The one-time-code login fails closed on any duplicate that slips
    /// through (it is a decoy for every holder) rather than guessing which tenant is meant.
    /// </summary>
    public bool RejectPhoneNumberHeldInAnotherTenant { get; set; }

    /// <summary>
    /// Validates that the configured policy values are internally consistent, throwing if any would
    /// produce a broken runtime (for example a zero lockout threshold that locks an account on the
    /// first wrong password). Called once at registration so a misconfiguration fails fast at startup.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="MaxFailedAccessAttempts"/> is below 1, <see cref="MaxLockoutDuration"/> is below
    /// <see cref="LockoutDuration"/> or above 365 days under <see cref="LockoutPolicy.Progressive"/>, or <see cref="LockoutDuration"/>,
    /// <see cref="DefaultTokenLifetime"/>, or <see cref="RefreshTokenLifetime"/> is not strictly positive.
    /// </exception>
    /// <exception cref="ArgumentException"><see cref="ConnectionStringName"/> is null, empty, or whitespace.</exception>
    public void Validate()
    {
        if (MaxFailedAccessAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxFailedAccessAttempts), MaxFailedAccessAttempts, "Must be at least 1.");
        }

        if (LockoutDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(LockoutDuration), LockoutDuration, "Must be a positive duration.");
        }

        if (LockoutPolicy == LockoutPolicy.Progressive && MaxLockoutDuration < LockoutDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxLockoutDuration), MaxLockoutDuration, "Must be at least LockoutDuration.");
        }

        // Without a ceiling a huge cap (TimeSpan.MaxValue) overflows DateTimeOffset when a lock is set, failing a
        // login attempt instead of startup.
        if (LockoutPolicy == LockoutPolicy.Progressive && MaxLockoutDuration > LongestAllowedMaxLockoutDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxLockoutDuration), MaxLockoutDuration, "Must be at most 365 days.");
        }

        if (DefaultTokenLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DefaultTokenLifetime), DefaultTokenLifetime, "Must be a positive duration.");
        }

        if (RefreshTokenLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RefreshTokenLifetime), RefreshTokenLifetime, "Must be a positive duration.");
        }

        if (string.IsNullOrWhiteSpace(ConnectionStringName))
        {
            throw new ArgumentException("Must not be null or whitespace.", nameof(ConnectionStringName));
        }
    }
}
