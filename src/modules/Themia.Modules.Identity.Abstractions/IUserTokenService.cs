using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.Abstractions;

/// <summary>Issues and consumes single-use, expiring user tokens (email/phone confirmation, password reset, 2FA).</summary>
public interface IUserTokenService
{
    /// <summary>Generates a token for a purpose and persists only its hash. The raw token is returned exactly once.</summary>
    /// <param name="userId">The owning user id.</param>
    /// <param name="purpose">What the token authorizes.</param>
    /// <param name="lifetime">An optional lifetime; defaults to <see cref="IdentityModuleOptions.DefaultTokenLifetime"/>.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The raw token value to deliver to the user.</returns>
    Task<string> GenerateAsync(Guid userId, TokenPurpose purpose, TimeSpan? lifetime = null, CancellationToken cancellationToken = default);

    /// <summary>Validates and consumes a token (single-use, expiry-checked). The presented raw token is
    /// SHA-256 hashed and matched against the stored <c>token_hash</c> by exact DB equality; because the
    /// stored value is a hash of a high-entropy random token, the equality match leaks nothing useful.</summary>
    /// <param name="userId">The owning user id.</param>
    /// <param name="purpose">The expected purpose.</param>
    /// <param name="rawToken">The raw token presented by the user.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The <see cref="TokenConsumeResult"/>.</returns>
    Task<TokenConsumeResult> ConsumeAsync(Guid userId, TokenPurpose purpose, string rawToken, CancellationToken cancellationToken = default);
}
