using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Themia.AspNetCore.Exceptions;
using Themia.Mediator.Abstractions;
using Themia.Mediator.Pipelines;
using Themia.MultiTenancy;
using Themia.MultiTenancy.Abstractions;

namespace Themia.MultiTenancy.Mediator;

/// <summary>
/// Mediator pipeline behavior that enforces tenant presence: an unauthenticated principal yields
/// <see cref="UnauthorizedException"/> (HTTP 401); an authenticated principal with no resolved tenant
/// yields <see cref="ForbiddenException"/> (HTTP 403) and a warning. A request implementing
/// <see cref="ISkipTenantValidation"/> bypasses the guard, and a principal in a configured
/// <see cref="TenantGuardOptions.PrivilegedRoles"/> bypasses the tenant check. Register it to run early
/// in the pipeline (execution order follows registration order).
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
public sealed class TenantGuardBehavior<TRequest, TResponse>(
    IHttpContextAccessor httpContextAccessor,
    ITenantAccessor tenantAccessor,
    IOptions<TenantGuardOptions> options,
    ILogger<TenantGuardBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <inheritdoc />
    public async Task<TResponse> HandleAsync(
        TRequest request,
        RequestHandlerContinuation<TResponse> next,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(next);

        var skip = request is ISkipTenantValidation;
        var principal = httpContextAccessor.HttpContext?.User;

        var verdict = TenantGuard.Evaluate(principal, tenantAccessor.Current, skip, options.Value.PrivilegedRoles);
        switch (verdict)
        {
            case TenantGuardVerdict.Unauthenticated:
                throw new UnauthorizedException("Authentication is required.");
            case TenantGuardVerdict.NoTenant:
                logger.LogWarning(
                    "Authenticated principal with no usable tenant for {RequestType} (UserId: {UserId}, Roles: {Roles})",
                    typeof(TRequest).Name, UserId(principal), Roles(principal));
                throw new ForbiddenException("A tenant context is required for this request.");
            default:
                return await next(cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? UserId(ClaimsPrincipal? principal) =>
        principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    private static string Roles(ClaimsPrincipal? principal) =>
        principal is null ? string.Empty : string.Join(",", principal.FindAll(ClaimTypes.Role).Select(c => c.Value));
}
