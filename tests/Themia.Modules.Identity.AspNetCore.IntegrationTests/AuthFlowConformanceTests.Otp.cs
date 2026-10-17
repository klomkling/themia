using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Auditing;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.AspNetCore.Endpoints;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.IntegrationTests;

// Passwordless phone-code login over HTTP against the real engine: the compare-and-set that counts a guess
// and consumes a code is a SQL UPDATE here, so these are the tests that prove parallel guesses cannot
// overrun the cap and a code cannot log in twice. They run once per peer (EF/PostgreSQL, Dapper/SQL Server).
public abstract partial class AuthFlowConformanceTests
{
    private const string OtpPhone = "0812345678";

    private static readonly JsonSerializerOptions OtpJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly RecordingOtpSender otpSender = new();

    [Fact]
    public async Task OtpLogin_StartThenVerify_IssuesTokensThatAuthenticate()
    {
        await ResetAsync();
        var userId = await SeedPhoneUserAsync("olivia", OtpPhone);

        var start = await OtpStartAsync(OtpPhone);
        var code = Assert.Single(otpSender.Codes);
        var (status, body) = await OtpVerifyAsync(start.ChallengeId, code);
        var (meStatus, me) = await GetMeAsync(body!.AccessToken);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(HttpStatusCode.OK, meStatus);
        Assert.Equal(userId, me!.UserId);
        var (refreshStatus, _) = await RefreshAsync(body.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, refreshStatus);
    }

    [Fact]
    public async Task OtpLogin_StartForAnUnknownPhone_HasTheSameShapeAndSendsNothing()
    {
        await ResetAsync();

        var start = await OtpStartAsync("0899999999");

        Assert.NotEqual(Guid.Empty, start.ChallengeId);
        Assert.Empty(otpSender.Codes);
        var (status, _) = await OtpVerifyAsync(start.ChallengeId, "000000");
        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task OtpLogin_ParallelVerifiesOfTheRightCode_LogInExactlyOnce()
    {
        await ResetAsync();
        await SeedPhoneUserAsync("paula", OtpPhone);
        var start = await OtpStartAsync(OtpPhone);
        var code = otpSender.Codes.Single();

        var results = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => OtpVerifyAsync(start.ChallengeId, code)));

        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.OK));
        Assert.All(results.Where(r => r.Status != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Unauthorized, r.Status));
    }

    [Fact]
    public async Task OtpLogin_ParallelWrongGuesses_NeverBeatTheAttemptCap_EvenWithTheRightCodeAfterwards()
    {
        await ResetAsync();
        await SeedPhoneUserAsync("quinn", OtpPhone);
        var start = await OtpStartAsync(OtpPhone);
        var code = otpSender.Codes.Single();
        var wrong = code == "000000" ? "111111" : "000000";

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => OtpVerifyAsync(start.ChallengeId, wrong)));
        var (status, _) = await OtpVerifyAsync(start.ChallengeId, code);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task OtpLogin_StartingAgainNeverResetsTheFailureCap()
    {
        await ResetAsync();
        await SeedPhoneUserAsync("rhea", OtpPhone);
        var wrong = "abcdef";

        // Default cap: 5 guesses per challenge, 10 failures per phone per window.
        for (var round = 0; round < 2; round++)
        {
            var start = await OtpStartAsync(OtpPhone);
            for (var i = 0; i < 5; i++)
            {
                await OtpVerifyAsync(start.ChallengeId, wrong);
            }
        }

        var response = await _client!.PostAsJsonAsync("/auth/otp/start", new { PhoneNumber = OtpPhone }, OtpJson);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task OtpLogin_AUserDeactivatedAfterTheCodeWasSent_GetsNoTokens()
    {
        await ResetAsync();
        var userId = await SeedPhoneUserAsync("sven", OtpPhone);
        var start = await OtpStartAsync(OtpPhone);
        var code = otpSender.Codes.Single();
        await SetActiveAsync(userId, false);

        var (status, body) = await OtpVerifyAsync(start.ChallengeId, code);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Null(body);
    }

    private async Task<OtpStartResponse> OtpStartAsync(string phone)
    {
        var response = await _client!.PostAsJsonAsync("/auth/otp/start", new { PhoneNumber = phone }, OtpJson);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<OtpStartResponse>(OtpJson))!;
    }

    private async Task<(HttpStatusCode Status, AuthResponse? Body)> OtpVerifyAsync(Guid challengeId, string code)
    {
        var response = await _client!.PostAsJsonAsync("/auth/otp/verify", new { ChallengeId = challengeId, Code = code }, OtpJson);
        AuthResponse? body = response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<AuthResponse>(OtpJson)
            : null;
        return (response.StatusCode, body);
    }

    private async Task<Guid> SeedPhoneUserAsync(string userName, string phone)
    {
        var userId = await SeedTenantUserAsync(userName, "Pass1234!");
        await using var provider = BuildSeedProvider();
        await using var scope = provider.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserService>();
        Assert.True((await users.SetPhoneNumberAsync(userId, phone)).Succeeded);
        Assert.True((await users.ConfirmPhoneNumberAsync(userId)).Succeeded);
        return userId;
    }

    private async Task SetActiveAsync(Guid userId, bool active)
    {
        await using var provider = BuildSeedProvider();
        await using var scope = provider.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserService>();
        Assert.True((await users.SetActiveAsync(userId, active)).Succeeded);
    }

    private ServiceProvider BuildSeedProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = ConnectionString })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddScoped<ITenantContext>(_ => new TenantContext(new TenantId("acme")));
        ConfigurePeer(services, configuration);
        RegisterIdentity(services, o => o.AllowPlatformLogin = false);
        services.RemoveAll<ICurrentUserAccessor>();
        services.AddSingleton<ICurrentUserAccessor>(new OtpSeedUserAccessor());
        return services.BuildServiceProvider();
    }
}

internal sealed class RecordingOtpSender : IOtpCodeSender
{
    private readonly List<string> codes = [];

    public IReadOnlyList<string> Codes
    {
        get { lock (codes) { return codes.ToList(); } }
    }

    public Task SendAsync(OtpDelivery delivery, CancellationToken cancellationToken = default)
    {
        lock (codes) { codes.Add(delivery.Code); }
        return Task.CompletedTask;
    }
}

file sealed class OtpSeedUserAccessor : ICurrentUserAccessor
{
    public string? UserId { get; } = "seed-user";
}
