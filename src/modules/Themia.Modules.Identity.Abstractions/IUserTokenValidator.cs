using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.Abstractions;

/// <summary>Checks a user token and names its owner without spending it. Kept apart from <see cref="IUserTokenService"/>
/// so adding it does not break that interface's implementers.</summary>
public interface IUserTokenValidator
{
    /// <summary>Looks a token up by its purpose and presented value alone and returns its owner, without consuming it
    /// or touching its expiry. Use it to run checks that need the user (for example a password policy against the
    /// user's e-mail) before <see cref="IUserTokenService.ConsumeAsync"/> spends the token; consuming stays the
    /// authorising step. The value is SHA-256 hashed and matched by exact equality, as in consume, so a guess has the
    /// same cost as a consume attempt. Neither call throttles: rate-limit both at the endpoint. The owner is returned
    /// only for a <see cref="TokenValidationOutcome.Valid"/> token that resolves in the ambient tenant or as a platform user.</summary>
    /// <param name="purpose">The expected purpose.</param>
    /// <param name="rawToken">The raw token presented by the user.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<TokenValidationResult> ValidateAsync(TokenPurpose purpose, string rawToken, CancellationToken cancellationToken = default);
}
