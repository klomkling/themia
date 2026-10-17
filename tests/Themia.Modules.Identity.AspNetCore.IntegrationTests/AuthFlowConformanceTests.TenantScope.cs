using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Auditing;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.IntegrationTests;

// coord #0166: with a claims-only tenant strategy an anonymous refresh, logout or OTP request has a null tenant
// scope. These run against a host whose tenant context is the ambient accessor (null here), on the real engines.
public abstract partial class AuthFlowConformanceTests
{
    private static readonly JsonSerializerOptions ScopeJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly List<IHost> extraHosts = [];

    private async Task<HttpClient> NewClaimsOnlyClientAsync()
    {
        var host = await BuildHostAsync(tenant: null, allowPlatformLogin: false, accessorBackedTenant: true);
        extraHosts.Add(host);
        return host.GetTestClient();
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body) =>
        await client.PostAsJsonAsync(path, body, ScopeJson);

    private static async Task<AuthResponse> ReadAuthAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>(ScopeJson))!;
    }

    private async Task<AuthResponse> LoginTenantUserAsync(string userName)
    {
        await SeedTenantUserAsync(userName, "Pass1234!");
        var (status, body) = await LoginAsync(userName, "Pass1234!");
        Assert.Equal(HttpStatusCode.OK, status);
        return body!;
    }

    [Fact]
    public async Task ClaimsOnly_RefreshOfATenantUsersToken_WorksWithNoScope()
    {
        await ResetAsync();
        var login = await LoginTenantUserAsync("claire");
        using var anonymous = await NewClaimsOnlyClientAsync();

        var refreshed = await ReadAuthAsync(await PostAsync(anonymous, "/auth/refresh", new { login.RefreshToken }));
        var me = await anonymous.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/me")
        {
            Headers = { Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", refreshed.AccessToken) },
        });
        var body = await me.Content.ReadFromJsonAsync<MeResponse>(ScopeJson);

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal("acme", body!.TenantId);
        Assert.NotEqual(login.RefreshToken, refreshed.RefreshToken);
    }

    [Fact]
    public async Task ClaimsOnly_LogoutRevokesTheSession_AndNeverReportsSuccessForNothing()
    {
        await ResetAsync();
        var login = await LoginTenantUserAsync("dmitri");
        using var anonymous = await NewClaimsOnlyClientAsync();

        var first = await PostAsync(anonymous, "/auth/logout", new { login.RefreshToken });
        var second = await PostAsync(anonymous, "/auth/logout", new { login.RefreshToken });
        var unknown = await PostAsync(anonymous, "/auth/logout", new { RefreshToken = "not-a-token" });
        var refreshAfter = await PostAsync(anonymous, "/auth/refresh", new { login.RefreshToken });

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refreshAfter.StatusCode);
    }

    [Fact]
    public async Task ClaimsOnly_LogoutAll_RevokesEverySessionOfTheOwner()
    {
        await ResetAsync();
        var first = await LoginTenantUserAsync("elena");
        var (_, secondLogin) = await LoginAsync("elena", "Pass1234!");
        using var anonymous = await NewClaimsOnlyClientAsync();

        var logout = await PostAsync(anonymous, "/auth/logout", new { first.RefreshToken, All = true });

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(anonymous, "/auth/refresh", new { secondLogin!.RefreshToken })).StatusCode);
    }

    [Fact]
    public async Task ClaimsOnly_RefreshOfADeactivatedUser_IsRefusedAndTheTokenIsNotConsumed()
    {
        await ResetAsync();
        var login = await LoginTenantUserAsync("farid");
        await DeactivateByNameAsync("farid");
        using var anonymous = await NewClaimsOnlyClientAsync();

        var response = await PostAsync(anonymous, "/auth/refresh", new { login.RefreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ClaimsOnly_OtpStartAndVerify_WorkForATenantUserWithNoScope()
    {
        await ResetAsync();
        await SeedPhoneUserAsync("greta", OtpPhone);
        using var anonymous = await NewClaimsOnlyClientAsync();

        var start = await PostAsync(anonymous, "/auth/otp/start", new { PhoneNumber = OtpPhone });
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var challenge = (await start.Content.ReadFromJsonAsync<Themia.Modules.Identity.AspNetCore.Endpoints.OtpStartResponse>(ScopeJson))!;
        var code = Assert.Single(otpSender.Codes);
        var auth = await ReadAuthAsync(await PostAsync(anonymous, "/auth/otp/verify", new { challenge.ChallengeId, Code = code }));
        var me = await anonymous.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/me")
        {
            Headers = { Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.AccessToken) },
        });

        Assert.Equal("acme", (await me.Content.ReadFromJsonAsync<MeResponse>(ScopeJson))!.TenantId);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(anonymous, "/auth/refresh", new { auth.RefreshToken })).StatusCode);
    }

    [Fact]
    public async Task ClaimsOnly_OtpOnAPhoneActiveInTwoTenants_IsADecoyAndSendsNothing()
    {
        await ResetAsync();
        await SeedPhoneUserAsync("hana", OtpPhone);
        var betaUser = await SeedUserAsync(new TenantId("beta"), "ivan", "Pass1234!", null, allowPlatformLogin: false);
        await using (var provider = BuildSeedProvider(new TenantId("beta")))
        await using (var scope = provider.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<IUserService>();
            Assert.True((await users.SetPhoneNumberAsync(betaUser, OtpPhone)).Succeeded);
            Assert.True((await users.ConfirmPhoneNumberAsync(betaUser)).Succeeded);
        }

        using var anonymous = await NewClaimsOnlyClientAsync();
        var start = await PostAsync(anonymous, "/auth/otp/start", new { PhoneNumber = OtpPhone });

        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        Assert.Empty(otpSender.Codes);
    }

    [Fact]
    public async Task ClaimsOnly_OtpStart_ForAnUnconfirmedPhone_IsADecoyAndSendsNothing()
    {
        await ResetAsync();
        var userId = await SeedTenantUserAsync("kira", "Pass1234!");
        await using (var provider = BuildSeedProvider())
        await using (var scope = provider.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<IUserService>().SetPhoneNumberAsync(userId, OtpPhone)).Succeeded);
        }

        using var anonymous = await NewClaimsOnlyClientAsync();
        var start = await PostAsync(anonymous, "/auth/otp/start", new { PhoneNumber = OtpPhone });

        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        Assert.NotEqual(Guid.Empty, (await start.Content.ReadFromJsonAsync<Themia.Modules.Identity.AspNetCore.Endpoints.OtpStartResponse>(ScopeJson))!.ChallengeId);
        Assert.Empty(otpSender.Codes);
    }

    [Fact]
    public async Task ClaimsOnly_OtpStart_ForADeactivatedUser_IsADecoyAndSendsNothing()
    {
        await ResetAsync();
        var userId = await SeedPhoneUserAsync("liam", OtpPhone);
        await SetActiveAsync(userId, false);

        using var anonymous = await NewClaimsOnlyClientAsync();
        var start = await PostAsync(anonymous, "/auth/otp/start", new { PhoneNumber = OtpPhone });

        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        Assert.Empty(otpSender.Codes);
    }

    [Fact]
    public async Task RevokeAllForUser_FromTheWrongScope_StillRevokesNothing()
    {
        await ResetAsync();
        var login = await LoginTenantUserAsync("jonas");
        var userId = await FindUserIdAsync("jonas");

        await using (var provider = BuildSeedProvider(new TenantId("beta")))
        await using (var scope = provider.CreateAsyncScope())
        {
            var refresh = scope.ServiceProvider.GetRequiredService<IRefreshTokenService>();
            Assert.Equal(0, await refresh.RevokeAllForUserAsync(userId));
        }

        using var anonymous = await NewClaimsOnlyClientAsync();
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(anonymous, "/auth/refresh", new { login.RefreshToken })).StatusCode);
    }

    private async Task<Guid> FindUserIdAsync(string userName)
    {
        await using var provider = BuildSeedProvider();
        await using var scope = provider.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IUserService>().FindByUserNameAsync(userName))!.Id;
    }

    private async Task DeactivateByNameAsync(string userName) => await SetActiveAsync(await FindUserIdAsync(userName), false);
}

internal sealed class AccessorBackedTenantContext : ITenantContext
{
    public TenantId? CurrentTenantId => TenantContextAccessor.CurrentTenantId;

    public string? Source => "accessor";
}
