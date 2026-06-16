using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

/// <summary>
/// Abstract base for in-process HTTP integration tests covering the headless external-login slice
/// (<c>POST /auth/external/{provider}</c>). Mirrors <see cref="AuthFlowConformanceTests"/>'s host
/// shape but additionally wires <c>AddThemiaExternalAuth().AddProvider(fake)</c> +
/// <c>MapIdentityExternalAuthEndpoints()</c>. Concrete subclasses supply the data peer (EF or Dapper,
/// PG or SqlServer). A deterministic in-test provider (<see cref="FakeExternalAuthProvider"/>) drives
/// subject/email/emailVerified from the posted <c>Code</c> — no real network.
/// </summary>
public abstract class ExternalAuthConformanceTests : IAsyncLifetime
{
    private const string TestIssuer = "themia-tests";
    private const string TestAudience = "themia-tests";

    // 32-byte (256-bit) HS256 symmetric signing key for test use only.
    private const string TestSigningKey = "themia-test-signing-key-32bytes!!";

    private const string FixedTenantId = "acme";
    private const string RedirectUri = "https://app.example.com/callback";

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
        _host = await BuildHostAsync(new TenantId(FixedTenantId));
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
    /// Builds an in-process host identical to <see cref="AuthFlowConformanceTests"/>'s, then layers on
    /// the external-login stack: <c>AddThemiaExternalAuth().AddProvider(fake)</c> and
    /// <c>MapGroup("/auth").MapIdentityExternalAuthEndpoints()</c>. The 0.5.1 password endpoints
    /// (<c>/auth/login|refresh|logout</c>) and the protected <c>GET /me</c> probe remain so external
    /// tokens can be exercised through the same surface.
    /// </summary>
    private Task<IHost> BuildHostAsync(TenantId? tenant)
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

                    ConfigurePeer(services, configuration);

                    services.AddThemiaIdentityServices(o => o.AllowPlatformLogin = true);
                    services.AddThemiaIdentityAuthorization();

                    services.AddThemiaIdentityAspNetCore(o =>
                    {
                        o.SigningKey = TestSigningKey;
                        o.Issuer = TestIssuer;
                        o.Audience = TestAudience;
                        o.AccessTokenLifetime = TimeSpan.FromMinutes(15);
                    });

                    // External-login: register the deterministic in-test provider named "fake".
                    services.AddThemiaExternalAuth().AddProvider(new FakeExternalAuthProvider());

                    services
                        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                        .AddThemiaJwtBearer();

                    services.AddAuthorization();
                    services.AddRouting();

                    services.RemoveAll<ITenantContext>();
                    services.AddScoped<ITenantContext>(_ => new TenantContext(tenant));
                });

                web.Configure(app =>
                {
                    app.UseThemiaProblemDetails();
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        var auth = endpoints.MapGroup("/auth");
                        auth.MapIdentityAuthEndpoints();
                        auth.MapIdentityExternalAuthEndpoints();

                        endpoints.MapGet("/me", (ICurrentUser u) =>
                                Results.Ok(new MeResponse(u.UserId, u.IsAuthenticated, u.TenantId, u.IsPlatform)))
                            .RequireAuthorization();
                    });
                });
            })
            .StartAsync();
    }

    // ── HTTP helpers ─────────────────────────────────────────────────────────

    private async Task<(HttpStatusCode Status, AuthResponse? Body)> ExternalLoginAsync(string provider, string code)
    {
        var response = await _client!.PostAsJsonAsync(
            $"/auth/external/{provider}", new { Code = code, RedirectUri }, JsonOpts);
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

    private async Task<(HttpStatusCode Status, AuthResponse? Body)> LoginAsync(string userName, string password)
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

    private async Task<(HttpStatusCode Status, MeResponse? Body)> GetMeAsync(string bearerToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/me");
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        var response = await _client!.SendAsync(request);
        MeResponse? body = null;
        if (response.IsSuccessStatusCode)
        {
            body = await response.Content.ReadFromJsonAsync<MeResponse>(JsonOpts);
        }
        return (response.StatusCode, body);
    }

    // The fake provider encodes "subject|email|emailVerified" in the authorization code. A trailing
    // pipe (empty email) and "false" verified flag are valid combinations.
    private static string Code(string subject, string? email = null, bool emailVerified = false) =>
        $"{subject}|{email}|{emailVerified.ToString().ToLowerInvariant()}";

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task External_login_new_subject_issues_tokens_and_authenticates()
    {
        await ResetAsync();

        var (status, body) = await ExternalLoginAsync("fake", Code("subject-new"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.NotNull(body);
        Assert.False(string.IsNullOrEmpty(body.AccessToken));
        Assert.False(string.IsNullOrEmpty(body.RefreshToken));

        // The issued access token must authenticate against the protected probe.
        var (meStatus, meBody) = await GetMeAsync(body.AccessToken);
        Assert.Equal(HttpStatusCode.OK, meStatus);
        Assert.NotNull(meBody);
        Assert.True(meBody.IsAuthenticated);
        Assert.NotNull(meBody.UserId);
    }

    [Fact]
    public async Task External_login_same_subject_resolves_to_the_same_user()
    {
        await ResetAsync();

        var (firstStatus, firstBody) = await ExternalLoginAsync("fake", Code("subject-stable"));
        Assert.Equal(HttpStatusCode.OK, firstStatus);
        Assert.NotNull(firstBody);
        var (_, firstMe) = await GetMeAsync(firstBody.AccessToken);
        Assert.NotNull(firstMe);

        // A second login with the SAME provider subject must reuse the existing link/user — no
        // duplicate provisioning. Equal /me UserId proves it is the same user.
        var (secondStatus, secondBody) = await ExternalLoginAsync("fake", Code("subject-stable"));
        Assert.Equal(HttpStatusCode.OK, secondStatus);
        Assert.NotNull(secondBody);
        var (_, secondMe) = await GetMeAsync(secondBody.AccessToken);
        Assert.NotNull(secondMe);

        Assert.Equal(firstMe.UserId, secondMe.UserId);
    }

    [Fact]
    public async Task External_login_with_verified_email_auto_links_existing_password_user()
    {
        await ResetAsync();

        // Seed a password user via the public login surface is not possible (no signup endpoint), so
        // provision the password user the same way the password tests do: through the store. Here we
        // reuse the external flow's own auto-link path by first creating the user via a verified-email
        // external login, then confirming a NEW subject with the SAME verified email resolves to it.
        // To keep this strictly about auto-link to a PASSWORD user, seed via the user service.
        var passwordUserId = await SeedPasswordUserAsync("linkme", "Pass1234!", "linkme@example.com");

        // New external subject, same verified email → auto-link to the seeded password user.
        var (status, body) = await ExternalLoginAsync(
            "fake", Code("subject-fresh", email: "linkme@example.com", emailVerified: true));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.NotNull(body);

        var (meStatus, meBody) = await GetMeAsync(body.AccessToken);
        Assert.Equal(HttpStatusCode.OK, meStatus);
        Assert.NotNull(meBody);
        Assert.Equal(passwordUserId, meBody.UserId);
    }

    [Fact]
    public async Task External_login_refresh_token_rotates_like_a_first_class_token()
    {
        await ResetAsync();

        var (loginStatus, loginBody) = await ExternalLoginAsync("fake", Code("subject-rotate"));
        Assert.Equal(HttpStatusCode.OK, loginStatus);
        Assert.NotNull(loginBody);

        // The external-issued refresh token must drive the standard 0.5.1 rotation endpoint.
        var (refreshStatus, refreshBody) = await RefreshAsync(loginBody.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, refreshStatus);
        Assert.NotNull(refreshBody);
        Assert.False(string.IsNullOrEmpty(refreshBody.AccessToken));
        Assert.NotEqual(loginBody.RefreshToken, refreshBody.RefreshToken);
    }

    [Fact]
    public async Task External_login_with_unknown_provider_returns_404()
    {
        await ResetAsync();

        var (status, _) = await ExternalLoginAsync("nope", Code("whoever"));
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    // ── Seed helper ───────────────────────────────────────────────────────────

    /// <summary>Seeds a tenant password user via a DI scope with the fixed tenant context, mirroring
    /// the seed path used by <see cref="AuthFlowConformanceTests"/>.</summary>
    private async Task<Guid> SeedPasswordUserAsync(string userName, string password, string email)
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
        services.AddSingleton<ICurrentUserAccessor>(new FixedSeedCurrentUserAccessor("seed-user"));

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserService>();

        var result = await users.CreateAsync(userName, password, email);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Seed user '{userName}' failed.");
        }

        var user = await users.FindByUserNameAsync(userName);
        return user!.Id;
    }
}

// ── Deterministic in-test external provider ───────────────────────────────────

/// <summary>A deterministic <see cref="IExternalAuthProvider"/> for tests. The authorization code
/// encodes <c>subject|email|emailVerified</c> (pipe-delimited); an empty email segment yields a null
/// email. Performs no network I/O.</summary>
file sealed class FakeExternalAuthProvider : IExternalAuthProvider
{
    public string Name => "fake";

    public Task<ExternalAuthResult> ExchangeAsync(
        ExternalAuthRequest request, CancellationToken cancellationToken = default)
    {
        var parts = request.Code.Split('|');
        var subject = parts.Length > 0 ? parts[0] : string.Empty;
        if (string.IsNullOrWhiteSpace(subject))
        {
            return Task.FromResult(ExternalAuthResult.Failed("missing subject"));
        }

        var email = parts.Length > 1 && !string.IsNullOrEmpty(parts[1]) ? parts[1] : null;
        var emailVerified = parts.Length > 2
            && bool.TryParse(parts[2], out var verified) && verified;

        var identity = new ExternalIdentity(Name, subject, email, emailVerified, DisplayName: null);
        return Task.FromResult(ExternalAuthResult.Success(identity));
    }
}

// ── Stub ICurrentUserAccessor for seed operations (no HttpContext) ────────────

file sealed class FixedSeedCurrentUserAccessor(string userId) : ICurrentUserAccessor
{
    public string? UserId { get; } = userId;
}
