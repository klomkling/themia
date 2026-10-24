using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.AspNetCore.Authentication;
using Themia.Modules.Identity.AspNetCore.DependencyInjection;
using Themia.Modules.Identity.DependencyInjection;
using Themia.Modules.Identity.Tokens.AspNetCore.Options;
using Themia.Modules.Identity.Services;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.Tests;

public sealed class EnabledLoginMethodsTests : IDisposable
{
    private const string RawPhone = "081-234-5678";
    private const string NormalizedPhone = "0812345678";
    private const LoginMethods AllMethods = LoginMethods.Password | LoginMethods.Otp | LoginMethods.External;

    private readonly TenantId? originalTenant = TenantContextAccessor.CurrentTenantId;
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-10-24T10:00:00Z"));

    public void Dispose() => TenantContextAccessor.CurrentTenantId = originalTenant;

    private (AuthenticationFlow Flow, FakeUserService Users, FakeRefreshTokenService Refresh, FakePasswordHasher Hasher, RecordingHooks Hooks)
        BuildPasswordFlow(LoginMethods enabled, PasswordVerificationResult verify)
    {
        var users = new FakeUserService { VerifyResult = verify, UserToReturn = new User { UserName = "alice" } };
        var refresh = new FakeRefreshTokenService();
        var hasher = new FakePasswordHasher();
        var hooks = new RecordingHooks { Refresh = refresh };
        var flow = new AuthenticationFlow(users, new FakeClaimsPrincipalFactory(), new FakeAccessTokenService(clock),
            refresh, hasher, hooks, [], new IdentityModuleOptions { EnabledLoginMethods = enabled }, clock,
            NullLogger<AuthenticationFlow>.Instance);
        return (flow, users, refresh, hasher, hooks);
    }

    private (OtpLoginFlow Flow, FakeOtpChallengeService Challenges, FakeOtpCodeSender Sender, FakeOtpRecipientResolver Resolver, FakeRefreshTokenService Refresh)
        BuildOtpFlow(LoginMethods enabled)
    {
        var user = new User
        {
            UserName = "alice", IsActive = true, PhoneNumber = RawPhone, NormalizedPhoneNumber = NormalizedPhone, PhoneNumberConfirmed = true,
        };
        var resolver = new FakeOtpRecipientResolver { Recipient = new OtpRecipient(user.Id, user.TenantId, RawPhone) };
        var users = new FakeUserService { UserToReturn = user };
        var challenges = new FakeOtpChallengeService
        {
            VerifyResult = new OtpVerifyResult(OtpVerifyOutcome.Verified, user.Id, NormalizedPhone) { TenantId = user.TenantId },
        };
        var sender = new FakeOtpCodeSender();
        var refresh = new FakeRefreshTokenService();
        var hooks = new RecordingHooks { Refresh = refresh };
        var flow = new OtpLoginFlow(users, resolver, challenges, sender, new FormattingOnlyPhoneNumberNormalizer(),
            new FakeClaimsPrincipalFactory(), new FakeAccessTokenService(clock), refresh, hooks, [],
            new IdentityModuleOptions { EnabledLoginMethods = enabled }, clock, NullLogger<OtpLoginFlow>.Instance);
        return (flow, challenges, sender, resolver, refresh);
    }

    [Fact]
    public void Options_enable_every_method_by_default()
    {
        Assert.Equal(AllMethods, new IdentityModuleOptions().EnabledLoginMethods);
    }

    [Fact]
    public void Options_bind_from_configuration_as_a_list_of_method_names()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["EnabledLoginMethods"] = "Password, External" })
            .Build();

        var options = new IdentityModuleOptions();
        config.Bind(options);

        Assert.Equal(LoginMethods.Password | LoginMethods.External, options.EnabledLoginMethods);
    }

    [Fact]
    public async Task PasswordLogin_WithPasswordOff_IssuesNoTokensEvenForTheRightPassword()
    {
        var (flow, users, refresh, hasher, _) = BuildPasswordFlow(LoginMethods.Otp | LoginMethods.External, PasswordVerificationResult.Success);

        var result = await flow.LoginAsync("alice", "correct-password");

        Assert.False(result.Succeeded);
        Assert.Equal(0, refresh.IssueCalls);
        Assert.Equal(0, users.VerifyCalls);
        Assert.Equal(0, hasher.HashCalls);
    }

    [Fact]
    public async Task PasswordLogin_WithPasswordOff_IsIndistinguishableFromAFailedLogin()
    {
        var (off, _, _, _, _) = BuildPasswordFlow(LoginMethods.Otp, PasswordVerificationResult.Success);
        var (on, _, _, _, _) = BuildPasswordFlow(AllMethods, PasswordVerificationResult.Failed);

        var disabled = await off.LoginAsync("alice", "correct-password");
        var failed = await on.LoginAsync("alice", "wrong-password");

        Assert.Equal(failed.Outcome, disabled.Outcome);
        Assert.False(disabled.TryGetTokens(out _));
    }

    [Fact]
    public async Task PasswordLogin_WithPasswordOn_StillSucceeds()
    {
        var (flow, _, refresh, _, _) = BuildPasswordFlow(LoginMethods.Password, PasswordVerificationResult.Success);

        var result = await flow.LoginAsync("alice", "pw");

        Assert.True(result.Succeeded);
        Assert.Equal(1, refresh.IssueCalls);
    }

    [Fact]
    public async Task Refresh_WithPasswordOff_IsNotGated()
    {
        var (flow, _, refresh, _, _) = BuildPasswordFlow(LoginMethods.Otp, PasswordVerificationResult.Success);
        refresh.RotateResult = RefreshValidationResult.Success(
            new User { UserName = "alice" }, new RefreshIssue("next", DateTimeOffset.UtcNow.AddDays(14), Guid.NewGuid()));

        var result = await flow.RefreshAsync("token");

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Logout_WithPasswordOff_StillRevokes()
    {
        var (flow, _, refresh, _, _) = BuildPasswordFlow(LoginMethods.Otp, PasswordVerificationResult.Success);

        await flow.LogoutAsync("token", allSessions: false);

        Assert.Equal(1, refresh.RevokeCalls);
    }

    [Fact]
    public async Task OtpStart_WithOtpOff_SendsNoCode()
    {
        var (flow, _, sender, _, _) = BuildOtpFlow(LoginMethods.Password | LoginMethods.External);

        await flow.StartAsync(RawPhone);

        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task OtpStart_WithOtpOff_AnswersLikeAPhoneWithNoAccount()
    {
        var (off, offChallenges, _, _, _) = BuildOtpFlow(LoginMethods.Password);
        var (on, onChallenges, _, onResolver, _) = BuildOtpFlow(AllMethods);
        onResolver.Recipient = null;

        var disabled = await off.StartAsync(RawPhone);
        var unknownPhone = await on.StartAsync(RawPhone);

        Assert.Equal(unknownPhone.Outcome, disabled.Outcome);
        Assert.NotEqual(Guid.Empty, disabled.ChallengeId);
        Assert.Null(offChallenges.LastIssueUserId);
        Assert.Equal(onChallenges.LastIssueUserId, offChallenges.LastIssueUserId);
    }

    [Fact]
    public async Task OtpVerify_WithOtpOff_IssuesNoTokensEvenForARightCode()
    {
        var (flow, _, _, _, refresh) = BuildOtpFlow(LoginMethods.Password | LoginMethods.External);

        var result = await flow.VerifyAsync(Guid.NewGuid(), "123456");

        Assert.False(result.Succeeded);
        Assert.Equal(0, refresh.IssueCalls);
    }

    [Fact]
    public async Task OtpVerify_WithOtpOff_IsIndistinguishableFromAWrongCode()
    {
        var (off, _, _, _, _) = BuildOtpFlow(LoginMethods.Password);
        var (on, onChallenges, _, _, _) = BuildOtpFlow(AllMethods);
        onChallenges.VerifyResult = new OtpVerifyResult(OtpVerifyOutcome.Invalid, null, null);

        var disabled = await off.VerifyAsync(Guid.NewGuid(), "123456");
        var wrong = await on.VerifyAsync(Guid.NewGuid(), "000000");

        Assert.Equal(wrong.Outcome, disabled.Outcome);
    }

    [Fact]
    public async Task OtpStart_WithOtpOn_StillSendsTheCode()
    {
        var (flow, _, sender, _, _) = BuildOtpFlow(LoginMethods.Otp);

        await flow.StartAsync(RawPhone);

        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task ResolvedFromTheContainer_TheRegisteredOptionsReachTheFlow()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IUserService>(new FakeUserService { UserToReturn = new User { UserName = "alice" } });
        services.AddSingleton<IClaimsPrincipalFactory, FakeClaimsPrincipalFactory>();
        services.AddSingleton<IAccessTokenService>(new FakeAccessTokenService(clock));
        services.AddSingleton<IRefreshTokenService, FakeRefreshTokenService>();
        services.AddSingleton<IPasswordHasher, FakePasswordHasher>();
        services.AddThemiaIdentityCore(o => o.EnabledLoginMethods = LoginMethods.Otp);
        services.AddThemiaIdentityAspNetCore(o =>
        {
            o.SigningKey = new string('k', 32);
            o.Issuer = "themia";
            o.Audience = "clients";
        });
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<IAuthenticationFlow>().LoginAsync("alice", "pw");

        Assert.False(result.Succeeded);
    }
}
