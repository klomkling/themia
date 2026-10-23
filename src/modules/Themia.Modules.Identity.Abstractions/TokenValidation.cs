namespace Themia.Modules.Identity.Abstractions;

/// <summary>The outcome of validating a user token without consuming it.</summary>
public enum TokenValidationOutcome
{
    /// <summary>The token exists for the purpose, has not expired and has not been consumed.</summary>
    Valid,

    /// <summary>No token matches the presented value for the purpose, or its owner is outside the ambient scope.</summary>
    NotFound,

    /// <summary>The token existed but has expired.</summary>
    Expired,

    /// <summary>The token was already consumed.</summary>
    AlreadyConsumed,
}

/// <summary>The result of <see cref="IUserTokenValidator.ValidateAsync"/>.</summary>
/// <param name="Outcome">What was found.</param>
/// <param name="UserId">The token's owner; set only when <paramref name="Outcome"/> is <see cref="TokenValidationOutcome.Valid"/>.</param>
public readonly record struct TokenValidationResult(TokenValidationOutcome Outcome, Guid? UserId)
{
    /// <summary>True when the token is valid and <see cref="UserId"/> is set.</summary>
    public bool IsValid => Outcome == TokenValidationOutcome.Valid;
}
