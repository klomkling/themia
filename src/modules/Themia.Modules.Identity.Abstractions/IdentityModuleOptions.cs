namespace Themia.Modules.Identity.Abstractions;

/// <summary>Tunable policy for the Identity module.</summary>
public sealed class IdentityModuleOptions
{
    /// <summary>The configuration connection-string name the schema migration runs against. Defaults to <c>"Default"</c>.</summary>
    public string ConnectionStringName { get; set; } = "Default";

    /// <summary>Consecutive failed password attempts before lockout engages. Defaults to 5.</summary>
    public int MaxFailedAccessAttempts { get; set; } = 5;

    /// <summary>How long an account stays locked out once the threshold is hit. Defaults to 15 minutes.</summary>
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Whether a platform (global) user may authenticate against a tenant entry point. Defaults to true.</summary>
    public bool AllowPlatformLogin { get; set; } = true;

    /// <summary>Default lifetime for generated <see cref="Entities.TokenPurpose"/> tokens. Defaults to 1 hour.</summary>
    public TimeSpan DefaultTokenLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Validates that the configured policy values are internally consistent, throwing if any would
    /// produce a broken runtime (for example a zero lockout threshold that locks an account on the
    /// first wrong password). Called once at registration so a misconfiguration fails fast at startup.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="MaxFailedAccessAttempts"/> is below 1, or <see cref="LockoutDuration"/> or
    /// <see cref="DefaultTokenLifetime"/> is not strictly positive.
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

        if (DefaultTokenLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DefaultTokenLifetime), DefaultTokenLifetime, "Must be a positive duration.");
        }

        if (string.IsNullOrWhiteSpace(ConnectionStringName))
        {
            throw new ArgumentException("Must not be null or whitespace.", nameof(ConnectionStringName));
        }
    }
}
