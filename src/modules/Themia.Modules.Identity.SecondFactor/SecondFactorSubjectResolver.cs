using System.Security.Claims;
using Themia.Modules.Identity.Abstractions;
using Themia.SecondFactor;

namespace Themia.Modules.Identity.SecondFactor;

/// <summary>Turns an Identity user id into the <see cref="MfaSubject"/> the second-factor layer evaluates. Replaceable through DI.</summary>
public interface ISecondFactorSubjectResolver
{
    /// <summary>Resolves the subject of <paramref name="userId"/> in the ambient tenant.</summary>
    /// <param name="userId">The Identity user id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The subject, or null when the user does not exist or is not active.</returns>
    Task<MfaSubject?> ResolveAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Reads the user's current security stamp, so a ticket issued before a password reset or sign-out-everywhere is refused before any proof is checked.</summary>
    /// <param name="userId">The Identity user id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The stamp, or null when the user does not exist or is not active.</returns>
    Task<string?> GetSecurityStampAsync(Guid userId, CancellationToken cancellationToken = default);
}

internal sealed class SecondFactorSubjectResolver(IUserService users, IClaimsPrincipalFactory principals) : ISecondFactorSubjectResolver
{
    private const string AuthenticationType = "Themia.SecondFactor";

    public async Task<string?> GetSecurityStampAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await users.FindByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        return user is { IsActive: true } ? user.SecurityStamp : null;
    }

    public async Task<MfaSubject?> ResolveAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await users.FindByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is not { IsActive: true })
        {
            return null;
        }

        var principal = await principals.CreateAsync(user, AuthenticationType, cancellationToken).ConfigureAwait(false);
        var roles = principal.FindAll(ClaimTypes.Role).Select(claim => claim.Value).ToHashSet(StringComparer.Ordinal);
        return new MfaSubject(user.Id.ToString("D"), user.TenantId?.Value, roles);
    }
}
