using Microsoft.AspNetCore.Http;

namespace Themia.Exceptional.AspNetCore;

/// <summary>
/// Opt-in browser access to the exceptions dashboard. A browser cannot send the Bearer header the host's API
/// authenticates with, so a staff call mints a single-use ticket (<c>POST {prefix}/ticket</c>) and the browser
/// redeems it (<c>GET {prefix}/session?ticket=…&amp;return=…</c>) for a cookie scoped to the dashboard prefix.
/// </summary>
/// <remarks>
/// Requirements, all checked when <c>MapThemiaExceptional</c> runs (startup does not guess): Themia.Challenges
/// registered with <see cref="ExceptionalBrowserAccess.AddExceptionalDashboardPurpose"/> called inside its
/// configure callback, and ASP.NET Core Data Protection registered with keys shared across nodes.
/// </remarks>
public sealed class ExceptionalBrowserAccessOptions
{
    /// <summary>Authenticates the mint call and returns the user id the ticket is bound to, or <see langword="null"/>
    /// to refuse. Apply the same permission check as <see cref="ExceptionalDashboardOptions.Authorize"/>. The mint
    /// route additionally requires an <c>Authorization</c> header, so a browser-attached cookie can never mint.</summary>
    public Func<HttpContext, Task<string?>>? MintUser { get; set; }

    /// <summary>Re-checks, on redeem and on EVERY cookie-authenticated dashboard request, that the user is still
    /// active and still holds the dashboard permission. Per request rather than per cookie lifetime, so
    /// deactivation or a revoked permission takes effect immediately.</summary>
    public Func<HttpContext, string, Task<bool>>? AuthorizeUser { get; set; }

    /// <summary>Absolute lifetime of the dashboard cookie (not sliding). Default 15 minutes.</summary>
    public TimeSpan CookieLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// SameSite of the dashboard cookie. Default <see cref="SameSiteMode.Strict"/>, correct when the web app and
    /// the API share a registrable domain (<c>app.example.com</c> and <c>app-api.example.com</c>). An app whose web
    /// and API are on different registrable domains must set <see cref="SameSiteMode.Lax"/>: a cross-site
    /// redirect would otherwise drop the cookie. Never guessed at startup.
    /// </summary>
    public SameSiteMode SameSite { get; set; } = SameSiteMode.Strict;
}
