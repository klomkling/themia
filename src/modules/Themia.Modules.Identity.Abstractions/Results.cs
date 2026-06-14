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
