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
}
