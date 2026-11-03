using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.AspNetCore.Authentication;
using Themia.Modules.Identity.Services;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.Tests;

/// <summary>
/// <see cref="IOtpLoginFlow.VerifyAsync(Guid, string, SessionRequest, CancellationToken)"/> (coord #0191): the one-time-code login
/// with every gate of the plain overload, minted on an app channel.
/// </summary>
public sealed class OtpLoginChannelTests : IDisposable
{
    private const string RawPhone = "081-234-5678";
    private const string NormalizedPhone = "0812345678";
    private const string Channel = "ezyassets.otp";

    private readonly TenantId? originalTenant = TenantContextAccessor.CurrentTenantId;
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-11-03T10:00:00Z"));
    private readonly IdentityModuleOptions options = new();
    private readonly FakeUserService users = new();
    private readonly FakeOtpChallengeService challenges = new();
    private readonly FakeRefreshTokenService refresh = new();
    private readonly RecordingHooks hooks = new();
    private readonly RecordingObserver observer = new();
    private readonly FakeClaimsPrincipalFactory principals = new();
    private readonly FakeAccessTokenService access;
    private readonly ScriptedSecondFactorPolicy policy = new();
    private readonly InMemoryUserTokenService userTokens;
    private readonly SecondFactorTicketService ticketService;
    private readonly OtpLoginFlow flow;

    public OtpLoginChannelTests()
    {
        access = new FakeAccessTokenService(clock);
        userTokens = new InMemoryUserTokenService(clock);
        ticketService = new SecondFactorTicketService(
            new EphemeralDataProtectionProvider(), new ServiceCollection().AddSingleton<IUserTokenService>(userTokens).BuildServiceProvider(), clock);
        hooks.Refresh = refresh;
        flow = new OtpLoginFlow(users, new FakeOtpRecipientResolver(), challenges, new FakeOtpCodeSender(), new FormattingOnlyPhoneNumberNormalizer(),
            principals, access, refresh, hooks, [observer], options, clock, NullLogger<OtpLoginFlow>.Instance, policy, ticketService);
    }

    public void Dispose() => TenantContextAccessor.CurrentTenantId = originalTenant;

    private static User Eligible(string? tenant = null) => new()
    {
        UserName = "alice", IsActive = true, PhoneNumber = RawPhone, NormalizedPhoneNumber = NormalizedPhone, PhoneNumberConfirmed = true,
        TenantId = tenant is null ? null : TenantId.From(tenant),
    };

    private static SessionRequest Session(string? tenant = null, string channel = Channel, IReadOnlyCollection<Claim>? claims = null) =>
        new(tenant is null ? null : TenantId.From(tenant), channel, claims);

    private User GivenVerifiedChallenge(User? user = null)
    {
        var u = user ?? Eligible();
        users.UserToReturn = u;
        challenges.VerifyResult = new OtpVerifyResult(OtpVerifyOutcome.Verified, u.Id, NormalizedPhone) { TenantId = u.TenantId };
        return u;
    }

    private SecondFactorCompletion Completion() =>
        new(ticketService, users, principals, access, refresh, hooks, [observer], options, clock, NullLogger<SecondFactorCompletion>.Instance, null);

    private GuardedAuthTokenIssuer Issuer() =>
        new(users, principals, access, refresh, policy, [observer], options, clock, NullLogger<GuardedAuthTokenIssuer>.Instance, ticketService);

    // ---- the happy path

    [Fact]
    public async Task Verify_mints_on_the_channel_with_sid_and_raises_both_events_once()
    {
        var user = GivenVerifiedChallenge();

        var result = await flow.VerifyAsync(Guid.NewGuid(), "123456", Session(), CancellationToken.None);

        Assert.Equal(LoginOutcome.Success, result.Outcome);
        Assert.Equal(1, refresh.IssueCalls);
        var issued = Assert.Single(observer.SessionsIssued);
        Assert.Equal((user.Id, Channel, refresh.LastFamilyId), issued);
        Assert.Equal(refresh.LastFamilyId.ToString(), access.LastPrincipal!.FindFirst(SessionClaims.SessionId)!.Value);
        Assert.Single(hooks.Calls, "login-succeeded");
        Assert.Single(observer.Calls, nameof(IIdentityEventObserver.OnLoginSucceededAsync));
    }

    [Fact]
    public async Task Verify_mints_for_a_tenant_user_when_the_session_names_the_challenges_tenant()
    {
        var user = GivenVerifiedChallenge(Eligible("acme"));

        var result = await flow.VerifyAsync(Guid.NewGuid(), "123456", Session("acme"), CancellationToken.None);

        Assert.Equal(LoginOutcome.Success, result.Outcome);
        Assert.Equal((user.Id, Channel, refresh.LastFamilyId), Assert.Single(observer.SessionsIssued));
    }

    [Fact]
    public async Task Completing_the_ticket_of_a_tenant_user_mints_on_the_channel()
    {
        var user = GivenVerifiedChallenge(Eligible("acme"));
        policy.Requirement = SecondFactorRequirement.Verify;
        var pending = (await flow.VerifyAsync(Guid.NewGuid(), "123456", Session("acme"), CancellationToken.None)).SecondFactor!.Value;

        var completed = await Completion().CompleteAsync(pending.Ticket, pending.UserId, pending.Stage);

        Assert.True(completed.Succeeded);
        Assert.Equal((user.Id, Channel, refresh.LastFamilyId), Assert.Single(observer.SessionsIssued));
        Assert.Single(hooks.Calls, "login-succeeded");
    }

    [Fact]
    public async Task Verify_asks_the_policy_as_Otp_with_the_channel_as_provider()
    {
        GivenVerifiedChallenge();

        await flow.VerifyAsync(Guid.NewGuid(), "123456", Session(), CancellationToken.None);

        var context = Assert.Single(policy.Contexts);
        Assert.Equal(SecondFactorMethod.Otp, context.Method);
        Assert.Equal(Channel, context.Provider);
    }

    // ---- the argument is checked before the challenge is read

    [Fact]
    public async Task Verify_throws_for_a_null_session_before_the_code_is_checked()
    {
        GivenVerifiedChallenge();

        await Assert.ThrowsAsync<ArgumentNullException>(() => flow.VerifyAsync(Guid.NewGuid(), "123456", null!, CancellationToken.None));

        Assert.Equal(0, challenges.VerifyCalls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Has Space")]
    [InlineData("UPPER")]
    public async Task Verify_throws_for_a_bad_channel_and_leaves_the_code_redeemable(string channel)
    {
        GivenVerifiedChallenge();

        await Assert.ThrowsAsync<ArgumentException>(() => flow.VerifyAsync(Guid.NewGuid(), "123456", Session(channel: channel), CancellationToken.None));

        Assert.Equal(0, challenges.VerifyCalls);
    }

    [Fact]
    public async Task Verify_throws_for_app_claims_and_leaves_the_code_redeemable()
    {
        GivenVerifiedChallenge();
        var claims = new[] { new Claim("app:role", "x") };

        await Assert.ThrowsAsync<ArgumentException>(() => flow.VerifyAsync(Guid.NewGuid(), "123456", Session(claims: claims), CancellationToken.None));

        Assert.Equal(0, challenges.VerifyCalls);
    }

    // ---- every gate of the plain overload

    [Fact]
    public async Task Verify_is_refused_without_a_guess_when_the_one_time_code_login_is_switched_off()
    {
        GivenVerifiedChallenge();
        options.EnabledLoginMethods = LoginMethods.Password;

        var result = await flow.VerifyAsync(Guid.NewGuid(), "123456", Session(), CancellationToken.None);

        Assert.Equal(LoginOutcome.InvalidCredentials, result.Outcome);
        Assert.Equal(0, challenges.VerifyCalls);
        Assert.Equal(0, refresh.IssueCalls);
    }

    [Fact]
    public async Task Verify_lets_the_before_login_hook_veto()
    {
        GivenVerifiedChallenge();
        hooks.DenyBeforeLogin = true;

        var result = await flow.VerifyAsync(Guid.NewGuid(), "123456", Session(), CancellationToken.None);

        Assert.Equal(LoginOutcome.Denied, result.Outcome);
        Assert.Equal(0, refresh.IssueCalls);
        Assert.Empty(observer.SessionsIssued);
    }

    [Fact]
    public async Task Verify_lets_the_login_succeeded_hook_veto_before_any_token_is_issued()
    {
        GivenVerifiedChallenge();
        hooks.DenyOnSucceeded = true;

        var result = await flow.VerifyAsync(Guid.NewGuid(), "123456", Session(), CancellationToken.None);

        Assert.Equal(LoginOutcome.Denied, result.Outcome);
        Assert.Equal(0, refresh.IssueCalls);
        Assert.Empty(observer.SessionsIssued);
    }

    [Fact]
    public async Task Verify_refuses_an_inactive_user()
    {
        var user = Eligible();
        user.IsActive = false;
        GivenVerifiedChallenge(user);

        var result = await flow.VerifyAsync(Guid.NewGuid(), "123456", Session(), CancellationToken.None);

        Assert.Equal(LoginOutcome.InvalidCredentials, result.Outcome);
        Assert.Equal(0, refresh.IssueCalls);
    }

    [Fact]
    public async Task Verify_refuses_a_locked_out_user()
    {
        var user = Eligible();
        user.LockoutEnd = clock.GetUtcNow().AddMinutes(10);
        GivenVerifiedChallenge(user);

        var result = await flow.VerifyAsync(Guid.NewGuid(), "123456", Session(), CancellationToken.None);

        Assert.Equal(LoginOutcome.LockedOut, result.Outcome);
        Assert.Equal(0, refresh.IssueCalls);
    }

    [Fact]
    public async Task Verify_refuses_when_the_user_changed_phone_after_the_code_was_sent()
    {
        var user = Eligible();
        GivenVerifiedChallenge(user);
        user.NormalizedPhoneNumber = "0899999999";

        var result = await flow.VerifyAsync(Guid.NewGuid(), "123456", Session(), CancellationToken.None);

        Assert.Equal(LoginOutcome.InvalidCredentials, result.Outcome);
        Assert.Equal(0, refresh.IssueCalls);
    }

    [Fact]
    public async Task Verify_refuses_a_platform_user_when_platform_login_is_off()
    {
        GivenVerifiedChallenge();
        options.AllowPlatformLogin = false;

        var result = await flow.VerifyAsync(Guid.NewGuid(), "123456", Session(), CancellationToken.None);

        Assert.Equal(LoginOutcome.InvalidCredentials, result.Outcome);
        Assert.Equal(0, refresh.IssueCalls);
    }

    // ---- the session's tenant against the challenge's, decided before any hook or policy

    [Fact]
    public async Task Verify_refuses_a_session_for_another_tenant_before_any_hook_or_the_policy_runs()
    {
        GivenVerifiedChallenge(Eligible("acme"));
        policy.Requirement = SecondFactorRequirement.Verify;

        var result = await flow.VerifyAsync(Guid.NewGuid(), "123456", Session("beta"), CancellationToken.None);

        Assert.Equal(LoginOutcome.InvalidCredentials, result.Outcome);
        Assert.Null(result.SecondFactor);
        Assert.Equal(0, policy.Calls);
        Assert.DoesNotContain("before-login", hooks.Calls);
        Assert.Equal(0, refresh.IssueCalls);
    }

    [Theory]
    [InlineData(SecondFactorRequirement.Verify)]
    [InlineData(SecondFactorRequirement.Enrol)]
    public async Task Verify_issues_no_ticket_for_a_session_of_another_tenant(SecondFactorRequirement requirement)
    {
        GivenVerifiedChallenge(Eligible("acme"));
        policy.Requirement = requirement;

        var result = await flow.VerifyAsync(Guid.NewGuid(), "123456", Session("beta"), CancellationToken.None);

        Assert.Null(result.SecondFactor);
        Assert.Equal(0, refresh.IssueCalls);
    }

    // ---- a second factor owed

    [Fact]
    public async Task Verify_that_owes_a_second_factor_issues_a_ticket_and_no_tokens()
    {
        var user = GivenVerifiedChallenge();
        policy.Requirement = SecondFactorRequirement.Verify;

        var result = await flow.VerifyAsync(Guid.NewGuid(), "123456", Session(), CancellationToken.None);

        Assert.Equal(LoginOutcome.SecondFactorRequired, result.Outcome);
        Assert.Equal(user.Id, result.SecondFactor!.Value.UserId);
        Assert.Equal(0, refresh.IssueCalls);
        Assert.Empty(observer.SessionsIssued);
        Assert.DoesNotContain("login-succeeded", hooks.Calls);
    }

    [Fact]
    public async Task Completing_the_ticket_mints_on_the_same_channel_once_with_both_events()
    {
        var user = GivenVerifiedChallenge();
        policy.Requirement = SecondFactorRequirement.Verify;
        var pending = (await flow.VerifyAsync(Guid.NewGuid(), "123456", Session(), CancellationToken.None)).SecondFactor!.Value;

        var completed = await Completion().CompleteAsync(pending.Ticket, pending.UserId, pending.Stage);

        Assert.True(completed.Succeeded);
        Assert.Equal(SecondFactorMethod.Otp, completed.Method);
        Assert.Equal((user.Id, Channel, refresh.LastFamilyId), Assert.Single(observer.SessionsIssued));
        Assert.Single(hooks.Calls, "login-succeeded");
        Assert.Single(observer.Calls, nameof(IIdentityEventObserver.OnLoginSucceededAsync));
        Assert.Equal(refresh.LastFamilyId.ToString(), access.LastPrincipal!.FindFirst(SessionClaims.SessionId)!.Value);
    }

    [Fact]
    public async Task Completing_the_ticket_twice_mints_once()
    {
        GivenVerifiedChallenge();
        policy.Requirement = SecondFactorRequirement.Verify;
        var pending = (await flow.VerifyAsync(Guid.NewGuid(), "123456", Session(), CancellationToken.None)).SecondFactor!.Value;

        await Completion().CompleteAsync(pending.Ticket, pending.UserId, pending.Stage);
        var second = await Completion().CompleteAsync(pending.Ticket, pending.UserId, pending.Stage);

        Assert.False(second.Succeeded);
        Assert.Equal(1, refresh.IssueCalls);
    }

    [Fact]
    public async Task The_guarded_issuer_refuses_and_burns_an_Otp_ticket_made_on_a_channel()
    {
        var user = GivenVerifiedChallenge();
        policy.Requirement = SecondFactorRequirement.Verify;
        var pending = (await flow.VerifyAsync(Guid.NewGuid(), "123456", Session(), CancellationToken.None)).SecondFactor!.Value;

        var viaIssuer = await Issuer().CompleteSessionAsync(user.Id, pending.Ticket, pending.Stage, Session(), CancellationToken.None);
        var afterwards = await Completion().CompleteAsync(pending.Ticket, pending.UserId, pending.Stage);

        Assert.Equal(IssueOutcome.TicketInvalid, viaIssuer.Outcome);
        Assert.False(afterwards.Succeeded);
        Assert.Equal(0, refresh.IssueCalls);
    }

    [Fact]
    public async Task An_external_ticket_still_completes_without_a_channel()
    {
        var user = Eligible();
        users.UserToReturn = user;
        var ticket = await ticketService.IssueAsync(
            new SecondFactorTicketData(user.Id, user.TenantId, SecondFactorMethod.External, "google", false, false, user.SecurityStamp, SecondFactorStage.Verify));
        var completion = new SecondFactorCompletion(
            ticketService, users, principals, access, refresh, hooks, [observer], options, clock, NullLogger<SecondFactorCompletion>.Instance, new RecordingExternalHooks());

        var completed = await completion.CompleteAsync(ticket.Ticket, user.Id, SecondFactorStage.Verify);

        Assert.True(completed.Succeeded);
        Assert.Empty(observer.SessionsIssued);
    }

    // ---- the plain overload is unchanged

    [Fact]
    public async Task The_plain_overload_still_mints_without_a_channel()
    {
        GivenVerifiedChallenge();

        var result = await flow.VerifyAsync(Guid.NewGuid(), "123456");

        Assert.Equal(LoginOutcome.Success, result.Outcome);
        Assert.Empty(observer.SessionsIssued);
    }
}
