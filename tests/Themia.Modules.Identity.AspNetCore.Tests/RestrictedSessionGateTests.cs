using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.AspNetCore.Authentication;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.Tests;

public sealed class RestrictedSessionGateTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid SessionId = Guid.NewGuid();

    private readonly InMemorySessionProofStore proofs = new();
    private readonly LiveSessions sessions = new();

    private static SessionProofSnapshot Row(SessionRestriction restriction) =>
        new("otp", SessionProofStrength.Weak, DateTimeOffset.UtcNow, restriction);

    [Theory]
    [InlineData("/plain")]
    [InlineData("/roles")]
    [InlineData("/named")]
    [InlineData("/inline")]
    public async Task Request_ShouldBeRefused_OnEveryKindOfAuthorizedEndpoint(string path)
    {
        proofs.Rows[(UserId, SessionId)] = Row(SessionRestriction.RecoveryOnly);

        using var response = await Send(path, "RecoveryOnly");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("second_factor_restricted", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Request_ShouldBeAllowed_OnAnEndpointMarkedAllowRestrictedSession()
    {
        proofs.Rows[(UserId, SessionId)] = Row(SessionRestriction.UpgradeDue);

        using var response = await Send("/allow", "UpgradeDue");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Request_ShouldBeAllowed_WhenTheRowSaysTheRestrictionWasLifted()
    {
        proofs.Rows[(UserId, SessionId)] = Row(SessionRestriction.None);

        using var response = await Send("/plain", "RecoveryOnly");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Request_ShouldNotTouchTheStore_WhenTheTokenIsNotRestricted()
    {
        using var response = await Send("/plain", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, proofs.Reads);
    }

    [Fact]
    public async Task Request_ShouldBeRefused_WhenTheSessionRowIsMissing()
    {
        using var response = await Send("/plain", "RecoveryOnly");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Request_ShouldBeRefused_WhenTheSessionIsNoLongerLive()
    {
        proofs.Rows[(UserId, SessionId)] = Row(SessionRestriction.None);
        sessions.Live = false;

        using var response = await Send("/plain", "RecoveryOnly");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Request_ShouldBeRefused_WhenTheStoreThrows()
    {
        proofs.Throw = new InvalidOperationException("db down");

        using var response = await Send("/plain", "RecoveryOnly");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("no-sid")]
    [InlineData("bad-sid")]
    [InlineData("no-user")]
    public async Task Request_ShouldBeRefused_WhenTheRestrictedTokenLacksAUsableSessionOrUser(string flaw)
    {
        proofs.Rows[(UserId, SessionId)] = Row(SessionRestriction.None);

        using var response = await Send("/plain", "RecoveryOnly", flaw);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Request_ShouldStillBeRefused_WhenTheAppSetsItsOwnFallbackPolicy()
    {
        proofs.Rows[(UserId, SessionId)] = Row(SessionRestriction.RecoveryOnly);

        using var response = await Send("/plain", "RecoveryOnly", withFallback: true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Request_ShouldKeepTheInnerHandlersResult_WhenAuthorizationFails()
    {
        using var response = await Send("/roles", null, role: "viewer");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("second_factor_restricted", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Request_ShouldGoThroughTheCustomHandlerRegisteredBeforeTheGate()
    {
        proofs.Rows[(UserId, SessionId)] = Row(SessionRestriction.None);
        CustomHandler.Calls = 0;

        using var response = await Send("/plain", "RecoveryOnly", customHandler: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, CustomHandler.Calls);
    }

    [Fact]
    public async Task Request_ShouldNotBeGated_OnAnEndpointWithoutAuthorization()
    {
        using var response = await Send("/open", "RecoveryOnly");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<HttpResponseMessage> Send(
        string path, string? restriction, string? flaw = null, bool withFallback = false, string role = "admin", bool customHandler = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ISessionProofStore>(proofs);
        builder.Services.AddSingleton<IRefreshTokenService>(sessions);
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Test", null);
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("named", policy => policy.RequireAuthenticatedUser());
            if (withFallback)
            {
                options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAssertion(_ => true).Build();
            }
        });
        if (customHandler)
        {
            builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, CustomHandler>();
        }

        builder.Services.AddThemiaRestrictedSessionGate();

        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/plain", () => "ok").RequireAuthorization();
        app.MapGet("/roles", () => "ok").RequireAuthorization(new AuthorizeAttribute { Roles = "admin" });
        app.MapGet("/named", () => "ok").RequireAuthorization("named");
        app.MapGet("/inline", () => "ok").RequireAuthorization(policy => policy.RequireClaim("uid"));
        app.MapGet("/allow", () => "ok").RequireAuthorization().WithMetadata(new AllowRestrictedSessionAttribute());
        app.MapGet("/open", () => "ok");
        await app.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Role", role);
        request.Headers.Add("X-Flaw", flaw ?? string.Empty);
        if (restriction is not null)
        {
            request.Headers.Add("X-Restr", restriction);
        }

        var response = await app.GetTestClient().SendAsync(request);
        await app.StopAsync();
        return response;
    }

    private sealed class HeaderAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var flaw = Request.Headers["X-Flaw"].ToString();
            var claims = new List<Claim> { new("uid", "1"), new(ClaimTypes.Role, Request.Headers["X-Role"].ToString()) };
            if (flaw != "no-user")
            {
                claims.Add(new Claim(ClaimTypes.NameIdentifier, UserId.ToString()));
            }

            if (flaw != "no-sid")
            {
                claims.Add(new Claim(SessionClaims.SessionId, flaw == "bad-sid" ? "nope" : SessionId.ToString()));
            }

            if (Request.Headers.TryGetValue("X-Restr", out var restriction))
            {
                claims.Add(new Claim("sf_restr", restriction.ToString()));
            }

            var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")), "Test");
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private sealed class CustomHandler : IAuthorizationMiddlewareResultHandler
    {
        internal static int Calls;

        public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
        {
            Interlocked.Increment(ref Calls);
            return new AuthorizationMiddlewareResultHandler().HandleAsync(next, context, policy, authorizeResult);
        }
    }

    private sealed class LiveSessions : IRefreshTokenService
    {
        public bool Live { get; set; } = true;

        public Task<bool> IsSessionLiveAsync(Guid userId, Guid familyId, CancellationToken cancellationToken = default) => Task.FromResult(Live);

        public Task<RefreshIssue> IssueAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RefreshValidationResult> ValidateAndRotateAsync(string rawToken, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> RevokeAsync(string rawToken, bool allForUser, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
