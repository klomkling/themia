using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Themia.Challenges;
using Xunit;

namespace Themia.Exceptional.AspNetCore.Tests;

public sealed class BrowserAccessTests
{
    private const string Staff = "staff-1";
    private const string SecretLogMarker = "boom-message-with-ticket";

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // Single-use opaque tokens held in memory; the real single-use/hash semantics are Themia.Challenges' own tests.
    private sealed class FakeChallenges(Clock clock) : IChallengeService
    {
        private readonly Dictionary<string, (ChallengeScope Scope, DateTimeOffset Expires)> _live = [];
        public bool RateLimit { get; set; }

        public Task<ChallengeIssueResult> IssueAsync(ChallengeScope scope, CancellationToken cancellationToken = default)
        {
            if (RateLimit) return Task.FromResult(ChallengeIssueResult.RateLimited(TimeSpan.FromSeconds(90)));
            var secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            _live[secret] = (scope, clock.Now + ExceptionalBrowserAccess.TicketLifetime);
            return Task.FromResult(ChallengeIssueResult.Issued(Guid.NewGuid(), secret, clock.Now + ExceptionalBrowserAccess.TicketLifetime));
        }

        public Task<ChallengeVerifyResult> VerifyByTokenAsync(string token, string purpose, string? tenantId = null, CancellationToken cancellationToken = default)
        {
            var unresolved = new ChallengeScope(ChallengeScope.UnresolvedKey, purpose);
            if (!_live.Remove(token, out var row) || row.Scope.Purpose != purpose) return Task.FromResult(ChallengeVerifyResult.NotFound(unresolved));
            return Task.FromResult(row.Expires <= clock.Now ? ChallengeVerifyResult.Expired(unresolved) : ChallengeVerifyResult.Verified(row.Scope));
        }

        public Task<ChallengeVerifyResult> VerifyAsync(ChallengeScope scope, string code, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ChallengeVerifyResult> PeekAsync(ChallengeScope scope, string secret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> InvalidateLiveAsync(ChallengeScope scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RefundAsync(Guid challengeId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    // Records only Themia.* categories: the framework's own "Request starting" line carries the raw URL, which is
    // the host's logging configuration to filter (documented), not something this package writes.
    private sealed class CapturingProvider : ILoggerProvider
    {
        public List<string> Lines { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Sink(categoryName, Lines);
        public void Dispose() { }

        private sealed class Sink(string category, List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (category.StartsWith("Themia.", StringComparison.Ordinal)) lines.Add(formatter(state, exception) + exception);
            }
        }
    }

    private sealed class Rig
    {
        public required HttpClient Client { get; init; }
        public required Clock Clock { get; init; }
        public required FakeChallenges Challenges { get; init; }
        public required CapturingProvider Logs { get; init; }
        public HashSet<string> Allowed { get; } = [Staff];
        public bool ThrowOnAuthorizeUser { get; set; }
        public int MintUserCalls { get; set; }
        public required IServiceProvider Services { get; init; }
    }

    private static async Task<Rig> StartAsync(bool browserAccess = true, SameSiteMode sameSite = SameSiteMode.Strict)
    {
        var clock = new Clock();
        var challenges = new FakeChallenges(clock);
        var logs = new CapturingProvider();
        Rig? rig = null;
        var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddDataProtection();
                    s.AddLogging(l => l.AddProvider(logs));
                    s.AddSingleton<TimeProvider>(clock);
                    s.AddSingleton<IExceptionStore>(new FakeExceptionStore());
                    s.AddSingleton(new ChallengeOptions().AddExceptionalDashboardPurpose());
                    s.AddSingleton<IChallengeService>(challenges);
                    s.AddSingleton(new ExceptionalOptions { ApplicationName = "app-a" });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapThemiaExceptional("/exceptions", o =>
                    {
                        o.EnableActions = true;
                        o.Authorize = ctx => Task.FromResult(ctx.Request.Headers.Authorization == "Bearer staff");
                        if (!browserAccess) return;
                        o.BrowserAccess = new ExceptionalBrowserAccessOptions
                        {
                            SameSite = sameSite,
                            MintUser = ctx =>
                            {
                                rig!.MintUserCalls++;
                                return Task.FromResult<string?>(ctx.Request.Headers["X-User"].FirstOrDefault());
                            },
                            AuthorizeUser = (_, user) => rig!.ThrowOnAuthorizeUser
                                ? throw new InvalidOperationException(SecretLogMarker)
                                : Task.FromResult(rig.Allowed.Contains(user)),
                        };
                    }));
                });
            })
            .StartAsync();
        rig = new Rig { Services = host.Services, Client = host.GetTestServer().CreateClient(), Clock = clock, Challenges = challenges, Logs = logs };
        return rig;
    }

    private static HttpRequestMessage Mint(string user = Staff)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/exceptions/ticket");
        req.Headers.Add("Authorization", "Bearer staff");
        req.Headers.Add("X-User", user);
        return req;
    }

    private static async Task<string> MintUrlAsync(Rig rig, string user = Staff)
    {
        var res = await rig.Client.SendAsync(Mint(user));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").GetProperty("redeemUrl").GetString()!;
    }

    private static string CookieOf(HttpResponseMessage res) =>
        res.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__Secure-themia_dash=", StringComparison.Ordinal));

    private static async Task<string> SessionCookieAsync(Rig rig)
    {
        var res = await rig.Client.GetAsync(await MintUrlAsync(rig));
        Assert.Equal(HttpStatusCode.SeeOther, res.StatusCode);
        return CookieOf(res).Split(';')[0];
    }

    private static HttpRequestMessage WithCookie(string path, string cookie)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add("Cookie", cookie);
        return req;
    }

    [Fact]
    public async Task Mint_ShouldReturnARelativeRedeemUrl()
    {
        var rig = await StartAsync();

        var url = await MintUrlAsync(rig);

        Assert.StartsWith("/exceptions/session?ticket=", url);
    }

    [Fact]
    public async Task Mint_ShouldReturn404_WhenNoAuthorizationHeaderIsSent()
    {
        var rig = await StartAsync();
        var cookie = await SessionCookieAsync(rig);
        var req = new HttpRequestMessage(HttpMethod.Post, "/exceptions/ticket");
        req.Headers.Add("Cookie", cookie);
        req.Headers.Add("X-User", Staff);

        var res = await rig.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Mint_ShouldReturn404_WhenMintUserRefuses()
    {
        var rig = await StartAsync();
        var req = new HttpRequestMessage(HttpMethod.Post, "/exceptions/ticket");
        req.Headers.Add("Authorization", "Bearer staff");

        var res = await rig.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Mint_ShouldReturn429WithRetryAfter_WhenRateLimited()
    {
        var rig = await StartAsync();
        rig.Challenges.RateLimit = true;

        var res = await rig.Client.SendAsync(Mint());

        Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
        Assert.Equal("90", res.Headers.GetValues("Retry-After").Single());
    }

    [Fact]
    public async Task Redeem_ShouldSetAScopedCookieAndRedirect()
    {
        var rig = await StartAsync();
        var id = Guid.NewGuid();
        var url = await MintUrlAsync(rig) + $"&return=/exceptions/{id}?page=2";

        var res = await rig.Client.GetAsync(url);

        Assert.Equal(HttpStatusCode.SeeOther, res.StatusCode);
        Assert.Equal($"/exceptions/{id}?page=2", res.Headers.Location!.OriginalString);
        var cookie = CookieOf(res);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/exceptions", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("no-referrer", res.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("no-store", res.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Redeem_ShouldHonourTheConfiguredSameSite()
    {
        var rig = await StartAsync(sameSite: SameSiteMode.Lax);

        var res = await rig.Client.GetAsync(await MintUrlAsync(rig));

        Assert.Contains("samesite=lax", CookieOf(res), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Redeem_ShouldReturn404_WhenTheTicketIsReused()
    {
        var rig = await StartAsync();
        var url = await MintUrlAsync(rig);
        await rig.Client.GetAsync(url);

        var second = await rig.Client.GetAsync(url);

        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.False(second.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Redeem_ShouldReturn404_WhenTheTicketExpired()
    {
        var rig = await StartAsync();
        var url = await MintUrlAsync(rig);
        rig.Clock.Now += ExceptionalBrowserAccess.TicketLifetime + TimeSpan.FromSeconds(1);

        var res = await rig.Client.GetAsync(url);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Theory]
    [InlineData("/exceptions/session?ticket=nope")]
    [InlineData("/exceptions/session")]
    public async Task Redeem_ShouldReturn404_WhenTheTicketIsUnknownOrMissing(string url)
    {
        var rig = await StartAsync();

        var res = await rig.Client.GetAsync(url);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Redeem_ShouldReturn404_WhenTheTicketIsOverLong()
    {
        var rig = await StartAsync();

        var res = await rig.Client.GetAsync("/exceptions/session?ticket=" + new string('a', ExceptionalBrowserAccess.MaxTicketLength + 1));

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Redeem_ShouldReturn404AndSetNoCookie_WhenThePermissionWasRevokedAfterMint()
    {
        var rig = await StartAsync();
        var url = await MintUrlAsync(rig);
        rig.Allowed.Clear();

        var res = await rig.Client.GetAsync(url);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.False(res.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Redeem_ShouldBindTheCookieToTheUserTheTicketWasMintedFor()
    {
        var rig = await StartAsync();
        rig.Allowed.Add("staff-2");
        rig.Allowed.Remove(Staff);

        var res = await rig.Client.GetAsync(await MintUrlAsync(rig, "staff-2"));
        var cookie = CookieOf(res).Split(';')[0];
        var list = await rig.Client.SendAsync(WithCookie("/exceptions", cookie));
        rig.Allowed.Remove("staff-2");
        var after = await rig.Client.SendAsync(WithCookie("/exceptions", cookie));

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }

    [Theory]
    [InlineData("//evil.com/x")]
    [InlineData("https://evil.com/x")]
    [InlineData("/\\evil.com")]
    [InlineData("/exceptions/../admin")]
    [InlineData("/exceptions//evil.com")]
    [InlineData("/exceptions/%2e%2e/admin")]
    [InlineData("/exceptions/a#frag")]
    [InlineData("/exceptionsX/other")]
    [InlineData("/other")]
    [InlineData("")]
    [InlineData("/exceptions/é")]
    public async Task Redeem_ShouldFallBackToThePrefix_WhenTheReturnPathIsNotUnderIt(string ret)
    {
        var rig = await StartAsync();
        var url = await MintUrlAsync(rig) + "&return=" + Uri.EscapeDataString(ret);

        var res = await rig.Client.GetAsync(url);

        Assert.Equal("/exceptions", res.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Cookie_ShouldOpenTheDashboard_ThenStopWorking_WhenItExpires()
    {
        var rig = await StartAsync();
        var cookie = await SessionCookieAsync(rig);
        var ok = await rig.Client.SendAsync(WithCookie("/exceptions", cookie));
        rig.Clock.Now += TimeSpan.FromMinutes(16);

        var expired = await rig.Client.SendAsync(WithCookie("/exceptions", cookie));

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
    }

    [Fact]
    public async Task Cookie_ShouldBeRejected_WhenTheUserIsDeactivated()
    {
        var rig = await StartAsync();
        var cookie = await SessionCookieAsync(rig);
        rig.Allowed.Clear();

        var res = await rig.Client.SendAsync(WithCookie("/exceptions", cookie));

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Cookie_ShouldBeRejected_WhenTampered()
    {
        var rig = await StartAsync();
        var cookie = await SessionCookieAsync(rig);

        var res = await rig.Client.SendAsync(WithCookie("/exceptions", cookie[..^3] + "AAA"));

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Cookie_ShouldStillRequireTheCsrfToken_OnActionPosts()
    {
        var rig = await StartAsync();
        var cookie = await SessionCookieAsync(rig);
        var req = new HttpRequestMessage(HttpMethod.Post, $"/exceptions/{Guid.NewGuid()}/protect") { Content = new FormUrlEncodedContent([]) };
        req.Headers.Add("Cookie", cookie);

        var res = await rig.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Redeem_ShouldNotLogTheTicketOrTheExceptionMessage_WhenACallbackThrows()
    {
        var rig = await StartAsync();
        var url = await MintUrlAsync(rig);
        var ticket = url[(url.IndexOf("ticket=", StringComparison.Ordinal) + 7)..];
        rig.ThrowOnAuthorizeUser = true;

        var res = await rig.Client.GetAsync(url);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.False(res.Headers.Contains("Set-Cookie"));
        Assert.DoesNotContain(rig.Logs.Lines, l => l.Contains(ticket, StringComparison.Ordinal) || l.Contains(SecretLogMarker, StringComparison.Ordinal));
        Assert.Contains(rig.Logs.Lines, l => l.Contains("InvalidOperationException", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Routes_ShouldNotExist_WhenBrowserAccessIsUnset()
    {
        var rig = await StartAsync(browserAccess: false);

        var mint = await rig.Client.SendAsync(Mint());
        var redeem = await rig.Client.GetAsync("/exceptions/session?ticket=x");

        Assert.NotEqual(HttpStatusCode.OK, mint.StatusCode); // POST falls into the {guid} route: 405
        Assert.DoesNotContain("redeemUrl", await mint.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, redeem.StatusCode);
    }

    [Fact]
    public async Task Bearer_ShouldStillOpenTheDashboard()
    {
        var rig = await StartAsync();
        var req = new HttpRequestMessage(HttpMethod.Get, "/exceptions");
        req.Headers.Add("Authorization", "Bearer staff");

        var res = await rig.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Mint_ShouldReturn404AndNotAskMintUser_WhenTheBearerGateDenies()
    {
        var rig = await StartAsync();
        var req = new HttpRequestMessage(HttpMethod.Post, "/exceptions/ticket");
        req.Headers.Add("Authorization", "Bearer wrong");
        req.Headers.Add("X-User", Staff);

        var res = await rig.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal(0, rig.MintUserCalls);
    }

    [Fact]
    public async Task Redeem_ShouldRefuseCrossSiteAndKeepTheTicket_WhenSameSiteIsStrict()
    {
        var rig = await StartAsync();
        var url = await MintUrlAsync(rig);
        var cross = new HttpRequestMessage(HttpMethod.Get, url);
        cross.Headers.Add("Sec-Fetch-Site", "cross-site");

        var refused = await rig.Client.SendAsync(cross);
        var sameSite = new HttpRequestMessage(HttpMethod.Get, url);
        sameSite.Headers.Add("Sec-Fetch-Site", "same-site");
        var accepted = await rig.Client.SendAsync(sameSite);

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.False(refused.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.SeeOther, accepted.StatusCode);
    }

    [Fact]
    public async Task Redeem_ShouldAcceptCrossSite_WhenSameSiteIsLax()
    {
        var rig = await StartAsync(sameSite: SameSiteMode.Lax);
        var req = new HttpRequestMessage(HttpMethod.Get, await MintUrlAsync(rig));
        req.Headers.Add("Sec-Fetch-Site", "cross-site");

        var res = await rig.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.SeeOther, res.StatusCode);
    }

    [Fact]
    public async Task Redeem_ShouldReturn404_WhenTheTicketBelongsToAnotherPurpose()
    {
        var rig = await StartAsync();
        var issued = await rig.Challenges.IssueAsync(new ChallengeScope(ExceptionalBrowserAccess.KeyPrefix + Staff, "other-purpose"));

        var res = await rig.Client.GetAsync("/exceptions/session?ticket=" + issued.Secret);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Theory]
    [InlineData("app-a", "/exceptions", HttpStatusCode.OK)]
    [InlineData("app-b", "/exceptions", HttpStatusCode.NotFound)]
    [InlineData("app-a", "/other", HttpStatusCode.NotFound)]
    [InlineData("", "/exceptions", HttpStatusCode.NotFound)]
    public async Task Cookie_ShouldOnlyOpenTheDashboard_WhenBoundToThisApplicationAndMount(string app, string mount, HttpStatusCode expected)
    {
        var rig = await StartAsync();
        var protector = rig.Services.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>()
            .CreateProtector(ExceptionalBrowserAccess.ProtectorPurpose(app, mount));
        var payload = JsonSerializer.Serialize(new { UserId = Staff, ExpiresAtUnix = rig.Clock.Now.AddMinutes(5).ToUnixTimeSeconds() });

        var res = await rig.Client.SendAsync(WithCookie("/exceptions", $"{ExceptionalBrowserAccess.CookieName}={protector.Protect(payload)}"));

        Assert.Equal(expected, res.StatusCode);
    }

    [Fact]
    public async Task Cookie_ShouldBeRejected_WhenProtectedForAnotherPurpose()
    {
        var rig = await StartAsync();
        var protector = rig.Services.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>().CreateProtector("something-else");
        var payload = JsonSerializer.Serialize(new { UserId = Staff, ExpiresAtUnix = rig.Clock.Now.AddMinutes(5).ToUnixTimeSeconds() });

        var res = await rig.Client.SendAsync(WithCookie("/exceptions", $"{ExceptionalBrowserAccess.CookieName}={protector.Protect(payload)}"));

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Dashboard_ShouldForbidFraming()
    {
        var rig = await StartAsync();
        var req = new HttpRequestMessage(HttpMethod.Get, "/exceptions");
        req.Headers.Add("Authorization", "Bearer staff");

        var res = await rig.Client.SendAsync(req);

        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("frame-ancestors 'none'", res.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public void Startup_ShouldFail_WhenNoChallengeEngineIsRegistered()
    {
        var services = new ServiceCollection().AddDataProtection().Services
            .AddSingleton(new ChallengeOptions().AddExceptionalDashboardPurpose()).BuildServiceProvider();
        var access = new ExceptionalBrowserAccessOptions { MintUser = _ => Task.FromResult<string?>(null), AuthorizeUser = (_, _) => Task.FromResult(true) };

        var ex = Assert.Throws<InvalidOperationException>(() => ExceptionalBrowserAccess.ValidateStartup(access, services));

        Assert.Contains("IChallengeService", ex.Message);
    }

    [Fact]
    public void Startup_ShouldFail_WhenACallbackIsMissing()
    {
        var services = new ServiceCollection().BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() =>
            ExceptionalBrowserAccess.ValidateStartup(new ExceptionalBrowserAccessOptions(), services));
    }

    [Fact]
    public void Startup_ShouldFail_WhenThePurposeWasNotConfigured()
    {
        var services = new ServiceCollection().AddDataProtection().Services
            .AddSingleton(new ChallengeOptions()).BuildServiceProvider();
        var access = new ExceptionalBrowserAccessOptions { MintUser = _ => Task.FromResult<string?>(null), AuthorizeUser = (_, _) => Task.FromResult(true) };

        Assert.Throws<InvalidOperationException>(() => ExceptionalBrowserAccess.ValidateStartup(access, services));
    }

    [Fact]
    public void Startup_ShouldFail_WhenChallengesIsNotRegistered()
    {
        var services = new ServiceCollection().AddDataProtection().Services.BuildServiceProvider();
        var access = new ExceptionalBrowserAccessOptions { MintUser = _ => Task.FromResult<string?>(null), AuthorizeUser = (_, _) => Task.FromResult(true) };

        Assert.Throws<InvalidOperationException>(() => ExceptionalBrowserAccess.ValidateStartup(access, services));
    }

    [Fact]
    public void Purpose_ShouldBeAnOpaqueShortLivedMultiTicketPurpose()
    {
        var purpose = new ChallengeOptions().AddExceptionalDashboardPurpose().GetPurpose(ExceptionalBrowserAccess.Purpose);

        Assert.Equal(ChallengeFormatKind.OpaqueToken, purpose.Format.Kind);
        Assert.Equal(TimeSpan.FromSeconds(60), purpose.Ttl);
        Assert.True(purpose.MaxLiveChallenges > 1);
    }

    [Fact]
    public void DefaultRedactor_ShouldMaskATicketValue()
    {
        Assert.Equal("***", ExceptionalOptions.DefaultRedactor("ticket", "abc"));
    }
}
