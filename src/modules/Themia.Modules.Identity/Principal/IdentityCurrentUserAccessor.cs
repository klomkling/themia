using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Themia.Framework.Data.Abstractions.Auditing;

namespace Themia.Modules.Identity.Principal;

/// <summary>Supplies the audit user id (<see cref="ICurrentUserAccessor"/>) from the authenticated principal, replacing the framework's null default.</summary>
public sealed class IdentityCurrentUserAccessor : ICurrentUserAccessor
{
    private readonly IHttpContextAccessor httpContextAccessor;

    /// <summary>Creates the accessor.</summary>
    public IdentityCurrentUserAccessor(IHttpContextAccessor httpContextAccessor)
    {
        ArgumentNullException.ThrowIfNull(httpContextAccessor);
        this.httpContextAccessor = httpContextAccessor;
    }

    /// <inheritdoc />
    public string? UserId =>
        httpContextAccessor.HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
}
