using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Tokens.AspNetCore.Authentication;

namespace Themia.Modules.Identity.AspNetCore.Authentication;

/// <summary>
/// Refuses a restricted session on every authorized endpoint that is not marked <see cref="AllowRestrictedSessionAttribute"/>.
/// Wraps the registered authorization result handler, so it runs after the endpoint's own policy has passed whatever that policy is.
/// </summary>
internal sealed class RestrictedSessionGate(IAuthorizationMiddlewareResultHandler inner, ILogger<RestrictedSessionGate> logger)
    : IAuthorizationMiddlewareResultHandler
{
    internal const string ProblemCode = "second_factor_restricted";

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Succeeded
            && context.User.HasClaim(claim => claim.Type == SessionProofClaims.RestrictionClaim)
            && context.GetEndpoint()?.Metadata.GetMetadata<AllowRestrictedSessionAttribute>() is null
            && await IsStillRestrictedAsync(context).ConfigureAwait(false))
        {
            await Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "A second factor must be enrolled first.",
                extensions: new Dictionary<string, object?> { ["code"] = ProblemCode })
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        await inner.HandleAsync(next, context, policy, authorizeResult).ConfigureAwait(false);
    }

    // The claim only says the token was minted restricted. The session row is the truth: an enrolment lifts it and the next request is
    // let through. Anything that cannot be confirmed as lifted counts as restricted (fail closed).
    private async Task<bool> IsStillRestrictedAsync(HttpContext context)
    {
        var principal = context.User;
        if (!Guid.TryParse(principal.GetSessionId(), out var sessionId)
            || !Guid.TryParse(UserId(principal), out var userId))
        {
            return true;
        }

        try
        {
            var services = context.RequestServices;
            var proofs = (ISessionProofStore?)services.GetService(typeof(ISessionProofStore));
            var tokens = (IRefreshTokenService?)services.GetService(typeof(IRefreshTokenService));
            if (proofs is null || tokens is null)
            {
                return true;
            }

            var ct = context.RequestAborted;
            if (!await tokens.IsSessionLiveAsync(userId, sessionId, ct).ConfigureAwait(false))
            {
                return true;
            }

            var row = await proofs.GetAsync(userId, sessionId, ct).ConfigureAwait(false);
            return row is null || row.Restriction != SessionRestriction.None;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The restricted-session check failed; the request was refused.");
            return true;
        }
    }

    private static string? UserId(ClaimsPrincipal principal) =>
        principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? principal.FindFirst("sub")?.Value;
}
