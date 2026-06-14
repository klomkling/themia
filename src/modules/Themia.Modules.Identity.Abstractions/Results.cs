namespace Themia.Modules.Identity.Abstractions;

/// <summary>The outcome of creating a user.</summary>
/// <param name="Succeeded">Whether the user was created.</param>
/// <param name="UserId">The new user's id when <paramref name="Succeeded"/> is true; otherwise null.</param>
/// <param name="Error">A stable error code when creation failed (e.g. <c>"duplicate_user_name"</c>, <c>"duplicate_email"</c>); otherwise null.</param>
public readonly record struct UserCreationResult(bool Succeeded, Guid? UserId, string? Error)
{
    /// <summary>Creates a success result.</summary>
    /// <param name="userId">The new user's identifier.</param>
    public static UserCreationResult Success(Guid userId) => new(true, userId, null);

    /// <summary>Creates a failure result.</summary>
    /// <param name="error">A stable error code.</param>
    public static UserCreationResult Failure(string error) => new(false, null, error);
}

/// <summary>The outcome of consuming a user token.</summary>
public enum TokenConsumeResult
{
    /// <summary>The token was valid and is now consumed.</summary>
    Success,

    /// <summary>No matching unconsumed token exists for the user and purpose.</summary>
    NotFound,

    /// <summary>The token existed but has expired.</summary>
    Expired,

    /// <summary>The token was already consumed.</summary>
    AlreadyConsumed,
}
