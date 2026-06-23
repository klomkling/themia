using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Themia.AspNetCore;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Auditing;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.AspNetCore.DependencyInjection;
using Themia.Modules.Identity.AspNetCore.Endpoints;
using Themia.Modules.Identity.DependencyInjection;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.IntegrationTests;

/// <summary>Response type returned by the /me probe endpoint.</summary>
public sealed record MeResponse(Guid? UserId, bool IsAuthenticated, string? TenantId, bool IsPlatform);

/// <summary>
/// Abstract base for in-process HTTP integration tests covering the full JWT auth flow.
/// Concrete subclasses supply the data peer (EF or Dapper, PG or SqlServer).
/// </summary>
public abstract class AuthFlowConformanceTests : IAsyncLifetime
{
    private const string TestIssuer = "themia-tests";
    private const string TestAudience = "themia-tests";

    // 32-byte (256-bit) HS256 symmetric signing key for test use only.
    private const string TestSigningKey = "themia-test-signing-key-32bytes!!";

    private const string FixedTenantId = "acme";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private IHost? _host;
    private HttpClient? _client;

    // ── Subclass contract ────────────────────────────────────────────────────

    protected abstract string ConnectionString { get; }

    /// <summary>Wires the data peer (EF or Dapper) against the test connection string.</summary>
    protected abstract void ConfigurePeer(IServiceCollection services, IConfiguration configuration);

    protected abstract Task ResetAsync();

    // ── IAsyncLifetime ───────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        _host = await BuildHostAsync(new TenantId(FixedTenantId), allowPlatformLogin: true);
        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    // ── Host factory ─────────────────────────────────────────────────────────

    /// <summary>
    /// Builds an in-process host using <see cref="HostBuilder"/> + TestServer, wired with:
    /// <list type="bullet">
    ///   <item>The data peer from <see cref="ConfigurePeer"/>.</item>
    ///   <item>A FIXED <see cref="ITenantContext"/> (registered last — overrides any framework one).</item>
    ///   <item><c>AddThemiaIdentityServices</c> + <c>AddThemiaIdentityAuthorization</c> + JWT.</item>
    ///   <item>JwtBearer via <c>AddThemiaJwtBearer</c>.</item>
    ///   <item>Pipeline: <c>UseThemiaProblemDetails</c> → <c>UseAuthentication</c> → <c>UseAuthorization</c>.</item>
    ///   <item><c>POST /auth/login|refresh|logout</c> + <c>GET /me</c> (protected probe).</item>
    /// </list>
    /// NOTE: <c>AddThemiaAspNetCore()</c> and <c>UseThemia()</c> are intentionally NOT called —
    /// both would register an <see cref="ITenantContext"/> that reads from request headers, conflicting
    /// with the fixed context. Only <c>UseThemiaProblemDetails()</c> is needed from the pipeline.
    /// </summary>
    private Task<IHost> BuildHostAsync(TenantId? tenant, bool allowPlatformLogin)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = ConnectionString,
            })
            .Build();

        return new HostBuilder()
            .ConfigureLogging(l => l.ClearProviders())
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton<IConfiguration>(configuration);

                    // Data peer first (same ordering as conformance tests).
                    ConfigurePeer(services, configuration);

                    // Identity store services + authorization (ICurrentUser from HttpContext).
                    services.AddThemiaIdentityServices(o =>
                    {
                        o.AllowPlatformLogin = allowPlatformLogin;
                    });
                    services.AddThemiaIdentityAuthorization();

                    // JWT token services + auth flow.
                    services.AddThemiaIdentityAspNetCore(o =>
                    {
                        o.SigningKey = TestSigningKey;
                        o.Issuer = TestIssuer;
                        o.Audience = TestAudience;
                        o.AccessTokenLifetime = TimeSpan.FromMinutes(15);
                    });

                    // JwtBearer validation.
                    services
                        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                        .AddThemiaJwtBearer();

                    services.AddAuthorization();
                    services.AddRouting();

                    // Fixed ITenantContext registered LAST so it overrides any prior registration.
                    services.RemoveAll<ITenantContext>();
                    services.AddScoped<ITenantContext>(_ => new TenantContext(tenant));
                });

                web.Configure(app =>
                {
                    // UseThemiaProblemDetails must be outermost so it catches exceptions from all
                    // downstream middleware including auth and endpoints.
                    app.UseThemiaProblemDetails();
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGroup("/auth").MapIdentityAuthEndpoints();

                        endpoints.MapGet("/me", (ICurrentUser u) =>
                                Results.Ok(new MeResponse(u.UserId, u.IsAuthenticated, u.TenantId, u.IsPlatform)))
                            .RequireAuthorization();

                        // Role-gated probe: proves [Authorize(Roles)] works through the short-claim
                        // JWT + the OnTokenValidated remap to ClaimTypes.Role.
                        endpoints.MapGet("/admin", () => Results.Ok())
                            .RequireAuthorization(p => p.RequireRole("admin"));
                    });
                });
            })
            .StartAsync();
    }

    // ── Seed helpers ─────────────────────────────────────────────────────────

    /// <summary>Seeds a tenant user via a DI scope with the fixed tenant context.</summary>
    private async Task<Guid> SeedTenantUserAsync(
        string userName,
        string password,
        string email = "user@example.com")
    {
        return await SeedUserAsync(new TenantId(FixedTenantId), userName, password, email, allowPlatformLogin: false);
    }

    /// <summary>Seeds a platform user (TenantId = null) via a DI scope with null tenant.</summary>
    private async Task<Guid> SeedPlatformUserAsync(string userName, string password)
    {
        return await SeedUserAsync(tenant: null, userName, password, email: null, allowPlatformLogin: true);
    }

    private async Task<Guid> SeedUserAsync(
        TenantId? tenant,
        string userName,
        string password,
        string? email,
        bool allowPlatformLogin)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = ConnectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddScoped<ITenantContext>(_ => new TenantContext(tenant));
        ConfigurePeer(services, configuration);
        services.AddThemiaIdentityServices(o => o.AllowPlatformLogin = allowPlatformLogin);
        services.RemoveAll<ICurrentUserAccessor>();
        services.AddSingleton<ICurrentUserAccessor>(new FixedCurrentUserAccessor("seed-user"));

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserService>();

        // email is optional in CreateAsync — passing null is fine.
        var result = await users.CreateAsync(userName, password, email);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Seed user '{userName}' failed.");
        }

        var user = await users.FindByUserNameAsync(userName);
        return user!.Id;
    }

    /// <summary>Creates (if needed) a role in the fixed tenant and assigns it to the user.</summary>
    private async Task AssignRoleAsync(Guid userId, string roleName)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = ConnectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddScoped<ITenantContext>(_ => new TenantContext(new TenantId(FixedTenantId)));
        ConfigurePeer(services, configuration);
        services.AddThemiaIdentityServices(o => o.AllowPlatformLogin = false);
        services.RemoveAll<ICurrentUserAccessor>();
        services.AddSingleton<ICurrentUserAccessor>(new FixedCurrentUserAccessor("seed-user"));

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();

        var roleId = await roles.CreateAsync(roleName)
            ?? (await roles.FindByNameAsync(roleName))!.Id;
        if (!await roles.AssignRoleAsync(userId, roleId))
        {
            throw new InvalidOperationException($"Assigning role '{roleName}' to user '{userId}' failed.");
        }
    }

    // ── HTTP helpers ─────────────────────────────────────────────────────────

    private async Task<(HttpStatusCode Status, AuthResponse? Body)> LoginAsync(
        string userName, string password)
    {
        var response = await _client!.PostAsJsonAsync(
            "/auth/login", new { UserName = userName, Password = password }, JsonOpts);
        AuthResponse? body = null;
        if (response.IsSuccessStatusCode)
        {
            body = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOpts);
        }
        return (response.StatusCode, body);
    }

    private async Task<(HttpStatusCode Status, AuthResponse? Body)> RefreshAsync(string refreshToken)
    {
        var response = await _client!.PostAsJsonAsync(
            "/auth/refresh", new { RefreshToken = refreshToken }, JsonOpts);
        AuthResponse? body = null;
        if (response.IsSuccessStatusCode)
        {
            body = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOpts);
        }
        return (response.StatusCode, body);
    }

    private async Task<HttpStatusCode> LogoutAsync(string refreshToken, bool all = false)
    {
        var body = all
            ? (object)new { RefreshToken = refreshToken, All = true }
            : new { RefreshToken = refreshToken };
        var response = await _client!.PostAsJsonAsync("/auth/logout", body, JsonOpts);
        return response.StatusCode;
    }

    private async Task<(HttpStatusCode Status, MeResponse? Body)> GetMeAsync(string? bearerToken = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/me");
        if (bearerToken is not null)
        {
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        }
        var response = await _client!.SendAsync(request);
        MeResponse? body = null;
        if (response.IsSuccessStatusCode)
        {
            body = await response.Content.ReadFromJsonAsync<MeResponse>(JsonOpts);
        }
        return (response.StatusCode, body);
    }

    private async Task<HttpStatusCode> GetWithBearerAsync(string path, string bearerToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        var response = await _client!.SendAsync(request);
        return response.StatusCode;
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Login_then_refresh_then_logout_succeeds()
    {
        await ResetAsync();
        await SeedTenantUserAsync("alice", "Pass1234!");

        // Login → 200 + auth response.
        var (loginStatus, loginBody) = await LoginAsync("alice", "Pass1234!");
        Assert.Equal(HttpStatusCode.OK, loginStatus);
        Assert.NotNull(loginBody);
        Assert.False(string.IsNullOrEmpty(loginBody.AccessToken));
        Assert.False(string.IsNullOrEmpty(loginBody.RefreshToken));

        var firstRefreshToken = loginBody.RefreshToken;

        // Refresh → 200 + NEW (rotated) refresh token.
        var (refreshStatus, refreshBody) = await RefreshAsync(firstRefreshToken);
        Assert.Equal(HttpStatusCode.OK, refreshStatus);
        Assert.NotNull(refreshBody);
        Assert.False(string.IsNullOrEmpty(refreshBody.RefreshToken));
        Assert.NotEqual(firstRefreshToken, refreshBody.RefreshToken);

        // Logout with the rotated token → 204 (no "all" flag in body; exercises the optional default).
        var logoutStatus = await LogoutAsync(refreshBody.RefreshToken);
        Assert.Equal(HttpStatusCode.NoContent, logoutStatus);

        // Post-logout: the revoked refresh token must be rejected → 401.
        var (postLogoutStatus, _) = await RefreshAsync(refreshBody.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, postLogoutStatus);
    }

    [Fact]
    public async Task Refresh_replay_after_rotation_is_rejected_and_revokes_family()
    {
        await ResetAsync();
        await SeedTenantUserAsync("bob", "Pass1234!");

        var (_, loginBody) = await LoginAsync("bob", "Pass1234!");
        Assert.NotNull(loginBody);
        var originalRefresh = loginBody.RefreshToken;

        // First legitimate rotation.
        var (rot1Status, rot1Body) = await RefreshAsync(originalRefresh);
        Assert.Equal(HttpStatusCode.OK, rot1Status);
        Assert.NotNull(rot1Body);
        var rotatedToken = rot1Body.RefreshToken;

        // Replay the already-consumed original token → 401.
        var (replayStatus, _) = await RefreshAsync(originalRefresh);
        Assert.Equal(HttpStatusCode.Unauthorized, replayStatus);

        // Family is revoked; the rotated successor must also fail → 401.
        var (revokedStatus, _) = await RefreshAsync(rotatedToken);
        Assert.Equal(HttpStatusCode.Unauthorized, revokedStatus);
    }

    [Fact]
    public async Task Logout_all_revokes_every_session()
    {
        await ResetAsync();
        await SeedTenantUserAsync("carol", "Pass1234!");

        // Two independent sessions.
        var (_, session1Body) = await LoginAsync("carol", "Pass1234!");
        var (_, session2Body) = await LoginAsync("carol", "Pass1234!");
        Assert.NotNull(session1Body);
        Assert.NotNull(session2Body);

        // Logout ALL using session 1's token.
        var logoutStatus = await LogoutAsync(session1Body.RefreshToken, all: true);
        Assert.Equal(HttpStatusCode.NoContent, logoutStatus);

        // Session 2's refresh token must be rejected.
        var (s2Status, _) = await RefreshAsync(session2Body.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, s2Status);

        // Session 1's token (used for logout) must also be gone.
        var (s1Status, _) = await RefreshAsync(session1Body.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, s1Status);
    }

    [Fact]
    public async Task All_credential_failures_return_identical_401()
    {
        await ResetAsync();
        await SeedTenantUserAsync("dave", "Pass1234!");

        // (a) Unknown user.
        var unknownResp = await _client!.PostAsJsonAsync(
            "/auth/login", new { UserName = "no-such-user", Password = "anything" }, JsonOpts);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownResp.StatusCode);
        var unknownProblem = await unknownResp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);

        // (b) Wrong password for a known user.
        var wrongPwResp = await _client!.PostAsJsonAsync(
            "/auth/login", new { UserName = "dave", Password = "WrongPassword!" }, JsonOpts);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPwResp.StatusCode);
        var wrongPwProblem = await wrongPwResp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);

        // Both 401 responses must carry identical status/title/detail — no user enumeration.
        // traceId is excluded as it is request-specific by design (different per-request trace).
        Assert.Equal(
            unknownProblem.GetProperty("status").GetInt32(),
            wrongPwProblem.GetProperty("status").GetInt32());
        Assert.Equal(
            unknownProblem.GetProperty("title").GetString(),
            wrongPwProblem.GetProperty("title").GetString());
        Assert.Equal(
            unknownProblem.GetProperty("detail").GetString(),
            wrongPwProblem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Login_with_blank_username_returns_400()
    {
        await ResetAsync();

        // A blank required field is malformed input → 400 (not 500 from a guard, not 401).
        var response = await _client!.PostAsJsonAsync(
            "/auth/login", new { UserName = "", Password = "Pass1234!" }, JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_with_blank_password_returns_400()
    {
        await ResetAsync();

        var response = await _client!.PostAsJsonAsync(
            "/auth/login", new { UserName = "alice", Password = "" }, JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_with_blank_token_returns_400()
    {
        await ResetAsync();

        var response = await _client!.PostAsJsonAsync(
            "/auth/refresh", new { RefreshToken = "" }, JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Logout_with_blank_token_returns_400()
    {
        await ResetAsync();

        var response = await _client!.PostAsJsonAsync(
            "/auth/logout", new { RefreshToken = "" }, JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Platform_user_can_log_in_when_allowed()
    {
        await ResetAsync();
        await SeedPlatformUserAsync("platform-root", "Pass1234!");

        // The host has AllowPlatformLogin=true and is scoped to the "acme" tenant.
        // Platform users are resolved cross-tenant, so login must succeed.
        var (loginStatus, loginBody) = await LoginAsync("platform-root", "Pass1234!");
        Assert.Equal(HttpStatusCode.OK, loginStatus);
        Assert.NotNull(loginBody);
        Assert.False(string.IsNullOrEmpty(loginBody.AccessToken));
    }

    [Fact]
    public async Task Bearer_token_populates_current_user()
    {
        await ResetAsync();
        var userId = await SeedTenantUserAsync("eve", "Pass1234!");

        var (_, loginBody) = await LoginAsync("eve", "Pass1234!");
        Assert.NotNull(loginBody);

        // GET /me with valid bearer → 200, IsAuthenticated=true, UserId matches.
        var (meStatus, meBody) = await GetMeAsync(loginBody.AccessToken);
        Assert.Equal(HttpStatusCode.OK, meStatus);
        Assert.NotNull(meBody);
        Assert.True(meBody.IsAuthenticated);
        Assert.Equal(userId, meBody.UserId);

        // The namespaced themia:tenant_id / themia:is_platform claims are minted verbatim and left
        // untouched by the OnTokenValidated remap, so ICurrentUser reads them on the bearer path.
        // A tenant-scoped user resolves to the host's fixed tenant ("acme") and is not a platform user.
        Assert.Equal(FixedTenantId, meBody.TenantId);
        Assert.False(meBody.IsPlatform);

        // GET /me with no token → 401.
        var (noTokenStatus, _) = await GetMeAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, noTokenStatus);
    }

    [Fact]
    public async Task Access_token_uses_standard_sub_claim_not_dotnet_uri()
    {
        await ResetAsync();
        var userId = await SeedTenantUserAsync("frank", "Pass1234!");

        var (_, loginBody) = await LoginAsync("frank", "Pass1234!");
        Assert.NotNull(loginBody);

        // The minted access token must carry the external-standard "sub" claim equal to the user id,
        // and must NOT carry the long .NET ClaimTypes.NameIdentifier URI.
        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(loginBody.AccessToken);
        Assert.Equal(userId.ToString(), jwt.GetClaim("sub").Value);
        Assert.DoesNotContain(jwt.Claims, c => c.Type == ClaimTypes.NameIdentifier);
    }

    [Fact]
    public async Task Role_gated_endpoint_authorizes_via_short_claim_jwt()
    {
        await ResetAsync();

        // Admin member: seed user, grant "admin" role.
        var adminId = await SeedTenantUserAsync("grace", "Pass1234!");
        await AssignRoleAsync(adminId, "admin");
        var (_, adminLogin) = await LoginAsync("grace", "Pass1234!");
        Assert.NotNull(adminLogin);

        // Non-member: seed user with no roles.
        await SeedTenantUserAsync("heidi", "Pass1234!", email: "heidi@example.com");
        var (_, nonMemberLogin) = await LoginAsync("heidi", "Pass1234!");
        Assert.NotNull(nonMemberLogin);

        // Admin bearer → 200 (the short "role" claim was remapped to ClaimTypes.Role on validation,
        // so [Authorize(Roles="admin")] / RequireRole("admin") is satisfied).
        var adminStatus = await GetWithBearerAsync("/admin", adminLogin.AccessToken);
        Assert.Equal(HttpStatusCode.OK, adminStatus);

        // Non-member bearer → 403.
        var nonMemberStatus = await GetWithBearerAsync("/admin", nonMemberLogin.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, nonMemberStatus);
    }
}

// ── Stub ICurrentUserAccessor for seed operations (no HttpContext) ────────────

file sealed class FixedCurrentUserAccessor(string userId) : ICurrentUserAccessor
{
    public string? UserId { get; } = userId;
}
