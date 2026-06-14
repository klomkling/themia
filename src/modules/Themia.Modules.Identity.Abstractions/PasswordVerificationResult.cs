namespace Themia.Modules.Identity.Abstractions;

/// <summary>The outcome of verifying a password against a user account.</summary>
public enum PasswordVerificationResult
{
    /// <summary>The password matched and the account may authenticate.</summary>
    Success,

    /// <summary>The password did not match.</summary>
    Failed,

    /// <summary>The account is locked out and cannot authenticate right now.</summary>
    LockedOut,

    /// <summary>The account is disabled.</summary>
    Inactive,

    /// <summary>No matching user exists.</summary>
    NotFound,
}
