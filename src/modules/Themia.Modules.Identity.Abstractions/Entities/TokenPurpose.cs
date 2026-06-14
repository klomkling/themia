namespace Themia.Modules.Identity.Abstractions.Entities;

/// <summary>The purpose a <see cref="UserToken"/> serves.</summary>
public enum TokenPurpose
{
    /// <summary>Confirms a user's email address.</summary>
    EmailConfirm,

    /// <summary>Confirms a user's phone number.</summary>
    PhoneConfirm,

    /// <summary>Authorizes a password reset.</summary>
    PasswordReset,

    /// <summary>A two-factor authentication challenge token.</summary>
    TwoFactor,
}
