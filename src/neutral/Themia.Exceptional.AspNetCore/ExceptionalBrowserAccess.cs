using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Themia.Challenges;

namespace Themia.Exceptional.AspNetCore;

/// <summary>Constants and setup helpers for <see cref="ExceptionalBrowserAccessOptions"/>.</summary>
public static class ExceptionalBrowserAccess
{
    /// <summary>The Themia.Challenges purpose the dashboard tickets use.</summary>
    public const string Purpose = "exceptional-dashboard";

    /// <summary>How long a minted ticket stays redeemable.</summary>
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(60);

    internal const string KeyPrefix = Purpose + ":";
    internal const string CookieName = "__Secure-themia_dash";
    internal const int MaxTicketLength = 256;
    internal const int MaxReturnLength = 512;

    /// <summary>
    /// Configures the ticket purpose: a 32-byte opaque token, 60 s TTL, several live tickets per user (opening two
    /// alerts in a row must not void the first) and a per-user mint window of 30 per 10 minutes. Call it inside the
    /// <c>AddThemiaChallenges</c> configure callback. The scope key is prefixed per user, so the Challenges per-key
    /// ceiling (shared across purposes) is not shared with other flows.
    /// </summary>
    /// <param name="options">The Challenges options being configured.</param>
    /// <returns><paramref name="options"/>, for chaining.</returns>
    public static ChallengeOptions AddExceptionalDashboardPurpose(this ChallengeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ConfigurePurpose(Purpose, p =>
        {
            p.Format = ChallengeFormat.OpaqueToken(32);
            p.Ttl = TicketLifetime;
            p.MaxLiveChallenges = 10;
            p.PerScopeWindow = (30, TimeSpan.FromMinutes(10));
        });
        return options;
    }

    internal static void ValidateStartup(ExceptionalBrowserAccessOptions access, IServiceProvider services)
    {
        if (access.MintUser is null || access.AuthorizeUser is null)
        {
            throw new InvalidOperationException(
                "ExceptionalDashboardOptions.BrowserAccess requires both MintUser and AuthorizeUser.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(access.CookieLifetime, TimeSpan.Zero);

        var challenges = services.GetService<ChallengeOptions>() ?? throw new InvalidOperationException(
            "ExceptionalDashboardOptions.BrowserAccess needs Themia.Challenges: call AddThemiaChallenges(...) with an engine package.");
        _ = challenges.GetPurpose(Purpose); // throws, naming the fix, when AddExceptionalDashboardPurpose() was not called

        if (services.GetService<IDataProtectionProvider>() is null)
        {
            throw new InvalidOperationException(
                "ExceptionalDashboardOptions.BrowserAccess needs ASP.NET Core Data Protection: call AddDataProtection() with keys shared across nodes.");
        }
    }

    // Only a path under the dashboard prefix, never an absolute URL, a protocol-relative one or a traversal.
    internal static string SafeReturn(string? requested, string prefix)
    {
        if (string.IsNullOrEmpty(requested) || requested.Length > MaxReturnLength || requested.Any(char.IsControl))
        {
            return prefix;
        }

        var queryAt = requested.IndexOf('?', StringComparison.Ordinal);
        var path = queryAt < 0 ? requested : requested[..queryAt];
        var underPrefix = path.Equals(prefix, StringComparison.Ordinal)
            || path.StartsWith(prefix + "/", StringComparison.Ordinal);
        var ambiguous = path.Contains('\\', StringComparison.Ordinal) || path.Contains('%', StringComparison.Ordinal)
            || path.Contains("//", StringComparison.Ordinal) || path.Contains("/.", StringComparison.Ordinal)
            || requested.Contains('#', StringComparison.Ordinal);
        return underPrefix && !ambiguous ? requested : prefix;
    }

    internal static async Task MintAsync(HttpContext ctx, ExceptionalDashboardOptions options, string prefix, CancellationToken ct)
    {
        var access = options.BrowserAccess!;
        Prevent(ctx.Response);
        if (!ctx.Request.Headers.ContainsKey("Authorization"))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var userId = await GuardAsync(ctx, () => access.MintUser!(ctx)).ConfigureAwait(false);
        if (string.IsNullOrEmpty(userId) || userId.Length > ChallengeScope.MaxKeyLength - KeyPrefix.Length)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var service = ctx.RequestServices.GetRequiredService<IChallengeService>();
        var issued = await service.IssueAsync(new ChallengeScope(KeyPrefix + userId, Purpose), ct).ConfigureAwait(false);
        if (!issued.Succeeded)
        {
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            if (issued.RetryAfter is { } retry)
            {
                ctx.Response.Headers.RetryAfter = ((int)Math.Ceiling(retry.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return;
        }

        // Relative on purpose: Request.Scheme/Host are the proxy-internal http view, never the public origin.
        var url = $"{ctx.Request.PathBase}{prefix}/session?ticket={Uri.EscapeDataString(issued.Secret!)}";
        await ctx.Response.WriteAsJsonAsync(new { data = new { redeemUrl = url } }, ct).ConfigureAwait(false);
    }

    internal static async Task RedeemAsync(HttpContext ctx, ExceptionalDashboardOptions options, string prefix, CancellationToken ct)
    {
        var access = options.BrowserAccess!;
        Prevent(ctx.Response);
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        try
        {
            var ticket = ctx.Request.Query["ticket"].ToString();
            if (ticket.Length is 0 or > MaxTicketLength)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var service = ctx.RequestServices.GetRequiredService<IChallengeService>();
            var result = await service.VerifyByTokenAsync(ticket, Purpose, null, ct).ConfigureAwait(false);
            var key = result.Scope.Key;
            if (result.Outcome != ChallengeVerifyOutcome.Verified || !key.StartsWith(KeyPrefix, StringComparison.Ordinal))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var userId = key[KeyPrefix.Length..];
            if (!await access.AuthorizeUser!(ctx, userId).ConfigureAwait(false))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            AppendCookie(ctx, access, prefix, userId);
            ctx.Response.StatusCode = StatusCodes.Status303SeeOther;
            ctx.Response.Headers.Location = ctx.Request.PathBase + SafeReturn(ctx.Request.Query["return"], prefix);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Type only: an exception message can embed the request URL, and the URL carries the ticket.
            ctx.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("Themia.Exceptional.AspNetCore")
                .LogError("Dashboard ticket redeem failed with {ExceptionType}; denying request.", ex.GetType().Name);
            if (!ctx.Response.HasStarted)
            {
                ctx.Response.Headers.Remove("Location");
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            }
        }
    }

    // Cookie path of Authorize: valid, unexpired, then the app re-checks the user on every request.
    internal static async Task<bool> CookieAuthorizedAsync(HttpContext ctx, ExceptionalBrowserAccessOptions access)
    {
        var value = ctx.Request.Cookies[CookieName];
        var protector = ctx.RequestServices.GetService<IDataProtectionProvider>()?.CreateProtector(CookieName);
        if (string.IsNullOrEmpty(value) || protector is null)
        {
            return false;
        }

        CookiePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<CookiePayload>(protector.Unprotect(value));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            return false;
        }

        var now = (ctx.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        if (payload is null || string.IsNullOrEmpty(payload.UserId) || payload.ExpiresAtUnix <= now.ToUnixTimeSeconds())
        {
            return false;
        }

        return await access.AuthorizeUser!(ctx, payload.UserId).ConfigureAwait(false);
    }

    private static void AppendCookie(HttpContext ctx, ExceptionalBrowserAccessOptions access, string prefix, string userId)
    {
        var now = (ctx.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        var expires = now + access.CookieLifetime;
        var protector = ctx.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector(CookieName);
        var value = protector.Protect(JsonSerializer.Serialize(new CookiePayload(userId, expires.ToUnixTimeSeconds())));
        ctx.Response.Cookies.Append(CookieName, value, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = access.SameSite,
            Path = ctx.Request.PathBase + prefix,
            Expires = expires,
            IsEssential = true,
        });
    }

    private static async Task<T?> GuardAsync<T>(HttpContext ctx, Func<Task<T?>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ctx.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("Themia.Exceptional.AspNetCore")
                .LogError("Dashboard ticket mint callback threw {ExceptionType}; denying request.", ex.GetType().Name);
            return default;
        }
    }

    private static void Prevent(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        response.Headers.Pragma = "no-cache";
    }

    private sealed record CookiePayload(string UserId, long ExpiresAtUnix);
}
