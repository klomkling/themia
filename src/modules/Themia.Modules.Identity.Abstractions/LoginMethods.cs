namespace Themia.Modules.Identity.Abstractions;

/// <summary>The ways a user can sign in. Used by <see cref="IdentityModuleOptions.EnabledLoginMethods"/> to
/// switch a method on or off for the whole host.</summary>
[Flags]
public enum LoginMethods
{
    /// <summary>No method; every sign-in is refused.</summary>
    None = 0,

    /// <summary>User name, email or phone plus a password.</summary>
    Password = 1,

    /// <summary>A one-time code sent to a confirmed phone.</summary>
    Otp = 2,

    /// <summary>An external identity provider, every registered provider together.</summary>
    External = 4,
}
