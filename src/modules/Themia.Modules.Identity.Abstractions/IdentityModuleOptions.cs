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

    /// <summary>
    /// The sign-in methods this host accepts. Defaults to every method. A method left out refuses
    /// exactly like a failed login: the password and external flows return their usual failure, a one-time-code start
    /// answers like a phone with no account and sends nothing, and a verify fails like a wrong code. Refresh and
    /// logout are never gated, so sessions already issued keep working and can still be ended. Host-wide only; binds
    /// from configuration as a list of names, for example <c>"Password, Otp"</c>.
    /// </summary>
    public LoginMethods EnabledLoginMethods { get; set; } = LoginMethods.Password | LoginMethods.Otp | LoginMethods.External;

    /// <summary>Default lifetime for generated <see cref="Entities.TokenPurpose"/> tokens. Defaults to 1 hour.</summary>
    public TimeSpan DefaultTokenLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// When <see langword="true"/>, the Themia JwtBearer scheme refuses an access token whose security-stamp claim no
    /// longer matches the user's stored stamp, or whose user is inactive or deleted, so a disable, delete, password
    /// change or role change takes effect on the next request instead of at token expiry. Costs one cached user read
    /// per <see cref="SecurityStampCacheDuration"/> per user. Tokens issued without the claim are refused once this is
    /// on. Defaults to false so upgrading does not sign existing sessions out.
    /// </summary>
    public bool ValidateSecurityStampOnEachRequest { get; set; }

    /// <summary>How long a stamp lookup is cached per user when <see cref="ValidateSecurityStampOnEachRequest"/> is on;
    /// this bounds how long a revoked token keeps working. <see cref="TimeSpan.Zero"/> disables the cache. Defaults to
    /// 30 seconds. The cache is per process, so each instance of a scaled-out API holds its own.</summary>
    public TimeSpan SecurityStampCacheDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The lifetime of an issued refresh token. Default 14 days.</summary>
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(14);

    /// <summary>
    /// How long after a refresh token has been rotated that presenting it again returns the <b>same</b> successor instead of treating it as
    /// theft and revoking the whole family. Default <see cref="TimeSpan.Zero"/>: off, a rotated token replayed at any time revokes its family.
    /// </summary>
    /// <remarks>
    /// For web apps that refresh per request with no shared memory: two near-simultaneous requests present the same refresh token and
    /// without a grace the second logs the user out everywhere. Recommended 30 seconds, at most <see cref="MaxRefreshReuseGrace"/>.
    /// <para>
    /// The trade-off: inside the window a captured token and the user's own token both yield the same successor, so the reuse
    /// tripwire does not fire for that window. After it, a replay revokes the family as before, as does a replay of a token whose
    /// successor has already been rotated again.
    /// </para>
    /// </remarks>
    public TimeSpan RefreshReuseGrace { get; set; } = TimeSpan.Zero;

    /// <summary>The longest <see cref="RefreshReuseGrace"/> accepted.</summary>
    public static readonly TimeSpan MaxRefreshReuseGrace = TimeSpan.FromMinutes(2);

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

        if (SecurityStampCacheDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SecurityStampCacheDuration), SecurityStampCacheDuration, "Must not be negative.");
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

        if (RefreshReuseGrace < TimeSpan.Zero || RefreshReuseGrace > MaxRefreshReuseGrace)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RefreshReuseGrace), RefreshReuseGrace, $"Must be between zero (off) and {MaxRefreshReuseGrace}.");
        }

        if (string.IsNullOrWhiteSpace(ConnectionStringName))
        {
            throw new ArgumentException("Must not be null or whitespace.", nameof(ConnectionStringName));
        }
    }
}
