using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Themia.Modules.Identity.Abstractions;

namespace Themia.Modules.Identity.Principal;

/// <summary>Default <see cref="ICurrentUser"/> reading the ambient principal from the current <see cref="HttpContext"/>.</summary>
public sealed class CurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor httpContextAccessor;

    /// <summary>Creates the accessor.</summary>
    public CurrentUser(IHttpContextAccessor httpContextAccessor)
    {
        ArgumentNullException.ThrowIfNull(httpContextAccessor);
        this.httpContextAccessor = httpContextAccessor;
    }

    private ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User;

    /// <inheritdoc />
    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    /// <inheritdoc />
    public Guid? UserId =>
        Guid.TryParse(Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    /// <inheritdoc />
    public string? TenantId => Principal?.FindFirst(IdentityClaimTypes.TenantId)?.Value;

    /// <inheritdoc />
    public bool IsPlatform =>
        IsAuthenticated
        && string.Equals(Principal?.FindFirst(IdentityClaimTypes.IsPlatform)?.Value, "true", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public string? UserName => Principal?.FindFirst(ClaimTypes.Name)?.Value;

    /// <inheritdoc />
    public IReadOnlyCollection<string> Roles =>
        Principal?.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray() ?? [];

    /// <inheritdoc />
    public IReadOnlyCollection<Claim> Claims => Principal?.Claims.ToArray() ?? [];

    /// <inheritdoc />
    public bool IsInRole(string role) => Principal?.IsInRole(role) == true;
}
