using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Themia.Content.AspNetCore;

/// <summary>Configures the admin endpoints mapped by <see cref="ContentEndpoints.MapThemiaContentAdminEndpoints"/>.</summary>
public sealed class ContentAdminOptions
{
    /// <summary>
    /// Decides whether a request may manage content pages. <b>Unset means every admin request is refused</b>, and so
    /// does a delegate that throws. Themia ships no permission catalog: check the consumer's own permission here — a
    /// policy is one <c>IAuthorizationService.AuthorizeAsync</c> call. The returned route group also accepts
    /// <c>RequireAuthorization(policy)</c>.
    /// </summary>
    public Func<HttpContext, Task<bool>>? Authorize { get; set; }

    /// <summary>Resolves the id stored as a revision's author. Defaults to the <see cref="ClaimTypes.NameIdentifier"/>
    /// claim.</summary>
    public Func<HttpContext, string?> ResolveEditorId { get; set; } =
        context => context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
}
