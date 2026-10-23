namespace Themia.Modules.Identity.Abstractions.Authentication;

/// <summary>Checks that the security stamp carried by an access token is still the user's current one, so a token
/// issued before a password change, role change, disable or delete is refused.</summary>
public interface ISecurityStampValidator
{
    /// <summary>True when the user exists, is active, and <paramref name="stamp"/> equals the stored stamp. The
    /// answer may be up to <see cref="IdentityModuleOptions.SecurityStampCacheDuration"/> old.</summary>
    /// <param name="userId">The token subject.</param>
    /// <param name="stamp">The stamp claim from the token; <see langword="null"/> or empty is never current.</param>
    /// <param name="cancellationToken">A token to cancel the lookup.</param>
    Task<bool> IsCurrentAsync(Guid userId, string? stamp, CancellationToken cancellationToken = default);
}
