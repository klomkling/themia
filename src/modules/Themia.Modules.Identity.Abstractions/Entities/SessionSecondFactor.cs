namespace Themia.Modules.Identity.Abstractions.Entities;

/// <summary>How much a session's second factor proves. Stored as an integer.</summary>
public enum SessionProofStrength
{
    /// <summary>Possession of a delivery channel (email, SMS).</summary>
    Weak = 0,

    /// <summary>Possession of a key or secret bound to the user (passkey, TOTP, recovery code).</summary>
    Strong = 1,
}

/// <summary>What a session may do until it enrols a strong second factor. Stored as an integer.</summary>
public enum SessionRestriction
{
    /// <summary>No restriction.</summary>
    None = 0,

    /// <summary>The session was opened with a recovery code: it may enrol a strong method but not remove or regenerate anything.</summary>
    RecoveryOnly = 1,

    /// <summary>The user's policy requires a stronger or spare method: enrol only, until a strong method is enrolled.</summary>
    UpgradeDue = 2,
}

/// <summary>The second-factor proof of one session, keyed by the session (refresh-token family) id. Read from the server and never
/// from a client claim. A session open at upgrade has no row, which means no second factor (fail closed). A parent-keyed child of
/// <see cref="User"/> with no tenant column: tenant isolation is enforced by resolving the owning user in scope.</summary>
public sealed class SessionSecondFactor
{
    /// <summary>The session id: the refresh-token family id (the <c>sid</c> claim). The Dapper layer maps columns to properties by name, so the key column is <c>id</c>.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning user id.</summary>
    public Guid UserId { get; set; }

    /// <summary>The authentication-method references proven, space separated (for example <c>otp mfa</c>).</summary>
    public string Amr { get; set; } = string.Empty;

    /// <summary>The strongest method proven.</summary>
    public SessionProofStrength Strength { get; set; }

    /// <summary>When the second factor was verified. The token <c>auth_time</c> is this value, unchanged by refresh.</summary>
    public DateTimeOffset AuthenticatedAt { get; set; }

    /// <summary>What the session may do until a strong method is enrolled.</summary>
    public SessionRestriction Restriction { get; set; }

    /// <summary>When the row was written.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the row last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
