namespace Themia.Modules.Identity.Abstractions;

/// <summary>The outcome of creating a user. Build instances via <see cref="Success"/> or <see cref="Failure"/>.</summary>
public readonly record struct UserCreationResult
{
    private UserCreationResult(bool succeeded, Guid? userId, string? error)
    {
        Succeeded = succeeded;
        UserId = userId;
        Error = error;
    }

    /// <summary>Whether the user was created.</summary>
    public bool Succeeded { get; }

    /// <summary>The new user's id when <see cref="Succeeded"/> is true; otherwise null.</summary>
    public Guid? UserId { get; }

    /// <summary>A stable error code when creation failed (e.g. <c>"duplicate_user_name"</c>, <c>"duplicate_email"</c>); otherwise null.</summary>
    public string? Error { get; }

    /// <summary>Creates a success result.</summary>
    /// <param name="userId">The new user's identifier.</param>
    public static UserCreationResult Success(Guid userId) => new(true, userId, null);

    /// <summary>Creates a failure result.</summary>
    /// <param name="error">A stable error code.</param>
    public static UserCreationResult Failure(string error) => new(false, null, error);
}

/// <summary>The outcome of setting a user's phone number.</summary>
/// <remarks>
/// An enum rather than a <see cref="bool"/>: <see cref="UserNotFound"/> and <see cref="Duplicate"/> need
/// different handling — one is a programming error, the other is something to show the user — and a bool
/// would collapse them into a single false that every call site is free to ignore. Adding a state later
/// then breaks an exhaustive <c>switch</c> instead of compiling silently at every existing caller.
/// </remarks>
public enum SetPhoneNumberResult
{
    /// <summary>The number was stored (or cleared) and <c>PhoneNumberConfirmed</c> was reset to false.</summary>
    Success,

    /// <summary>No such user in the ambient tenant or the platform scope.</summary>
    UserNotFound,

    /// <summary>Another user in the same scope already holds this number in its normalized form.</summary>
    Duplicate,
}

/// <summary>The outcome of setting a user's email address.</summary>
/// <remarks>Shaped like <see cref="SetPhoneNumberResult"/>, and for the same reasons — see its remarks.</remarks>
public enum SetEmailResult
{
    /// <summary>The address was stored (or cleared) and <c>EmailConfirmed</c> was reset to false.</summary>
    Success,

    /// <summary>No such user in the ambient tenant or the platform scope.</summary>
    UserNotFound,

    /// <summary>Another user in the same scope already holds this address in its normalized form.</summary>
    Duplicate,
}

/// <summary>The outcome of consuming a user token.</summary>
public enum TokenConsumeResult
{
    /// <summary>The token was valid and is now consumed.</summary>
    Success,

    /// <summary>No token whose hash matches the presented value exists for the user and purpose.</summary>
    NotFound,

    /// <summary>The token existed but has expired.</summary>
    Expired,

    /// <summary>The token was already consumed.</summary>
    AlreadyConsumed,
}
