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

public sealed class SecondFactorTests : IDisposable
{
    private const string RawPhone = "081-234-5678";
    private const string NormalizedPhone = "0812345678";

    private readonly TenantId? originalTenant = TenantContextAccessor.CurrentTenantId;
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-10-31T10:00:00Z"));
    private readonly FakeUserService users = new();
    private readonly FakeRefreshTokenService refresh = new();
    private readonly RecordingHooks hooks = new();
    private readonly RecordingExternalHooks externalHooks = new();
    private readonly RecordingObserver observer = new();
    private readonly FakeClaimsPrincipalFactory principals = new();
    private readonly FakeAccessTokenService access;
    private readonly InMemoryUserTokenService userTokens;
    private readonly SecondFactorTicketService ticketService;
    private readonly ScriptedSecondFactorPolicy policy = new();
    private readonly IdentityModuleOptions options = new();

    public SecondFactorTests()
    {
        access = new FakeAccessTokenService(clock);
        userTokens = new InMemoryUserTokenService(clock);
        ticketService = new SecondFactorTicketService(new EphemeralDataProtectionProvider(), TokenServices(userTokens), clock);
        hooks.Refresh = refresh;
    }

    public void Dispose() => TenantContextAccessor.CurrentTenantId = originalTenant;

    private static IServiceProvider TokenServices(IUserTokenService tokens) =>
        new ServiceCollection().AddSingleton(tokens).BuildServiceProvider();

    private AuthenticationFlow PasswordFlow(ISecondFactorTicketService? tickets = null) =>
        new(users, principals, access, refresh, new FakePasswordHasher(), hooks, [observer], options, clock,
            NullLogger<AuthenticationFlow>.Instance, policy, tickets ?? ticketService);

    private SecondFactorCompletion Completion() =>
        new(ticketService, users, principals, access, refresh, hooks, [observer], options, clock,
            NullLogger<SecondFactorCompletion>.Instance, externalHooks);

    private static User Staff() => new()
    {
        UserName = "staff", IsActive = true, PhoneNumber = RawPhone, NormalizedPhoneNumber = NormalizedPhone, PhoneNumberConfirmed = true,
    };

    private async Task<PendingSecondFactor> PendingPasswordLoginAsync(User? user = null)
    {
        users.UserToReturn = user ?? Staff();
        policy.Requirement = SecondFactorRequirement.Verify;
        var result = await PasswordFlow().LoginAsync("staff", "pw");
        return result.SecondFactor!.Value;
    }

    // ---- the seam in the password flow

    [Theory]
    [InlineData(SecondFactorRequirement.Verify, SecondFactorStage.Verify)]
    [InlineData(SecondFactorRequirement.Enrol, SecondFactorStage.Enrol)]
    public async Task Password_login_that_owes_a_second_factor_issues_a_ticket_and_nothing_else(SecondFactorRequirement requirement, SecondFactorStage stage)
    {
        users.UserToReturn = Staff();
        policy.Requirement = requirement;

        var result = await PasswordFlow().LoginAsync("staff", "pw");

        Assert.Equal(LoginOutcome.SecondFactorRequired, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.Null(result.Tokens);
        var pending = result.SecondFactor!.Value;
        Assert.Equal(stage, pending.Stage);
        Assert.Equal(users.UserToReturn!.Id, pending.UserId);
        Assert.False(string.IsNullOrWhiteSpace(pending.Ticket));
        Assert.Equal(0, refresh.IssueCalls);
        Assert.DoesNotContain("login-succeeded", hooks.Calls);
        Assert.DoesNotContain("login-failed", hooks.Calls);
        Assert.Empty(observer.Calls);
    }

    [Fact]
    public async Task Password_login_with_the_default_policy_is_unchanged()
    {
        users.UserToReturn = Staff();
        policy.Requirement = SecondFactorRequirement.NotRequired;

        var result = await PasswordFlow().LoginAsync("staff", "pw");

        Assert.True(result.Succeeded);
        Assert.Null(result.SecondFactor);
        Assert.Equal(1, refresh.IssueCalls);
    }

    [Fact]
    public async Task A_wrong_password_never_reaches_the_policy()
    {
        users.UserToReturn = Staff();
        users.VerifyResult = PasswordVerificationResult.Failed;
        policy.Requirement = SecondFactorRequirement.Verify;

        var result = await PasswordFlow().LoginAsync("staff", "wrong");

        Assert.Equal(LoginOutcome.InvalidCredentials, result.Outcome);
        Assert.Equal(0, policy.Calls);
    }

    [Fact]
    public async Task A_policy_that_throws_stops_the_login_with_nothing_issued()
    {
        users.UserToReturn = Staff();
        policy.Throw = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => PasswordFlow().LoginAsync("staff", "pw"));

        Assert.Equal(0, refresh.IssueCalls);
        Assert.DoesNotContain("login-succeeded", hooks.Calls);
        Assert.DoesNotContain("login-failed", hooks.Calls);
    }

    [Fact]
    public async Task A_policy_that_wants_a_second_factor_with_no_ticket_service_fails_closed()
    {
        users.UserToReturn = Staff();
        policy.Requirement = SecondFactorRequirement.Verify;
        var flow = new AuthenticationFlow(users, principals, access, refresh, new FakePasswordHasher(), hooks, [observer], options, clock,
            NullLogger<AuthenticationFlow>.Instance, policy, null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => flow.LoginAsync("staff", "pw"));

        Assert.Equal(0, refresh.IssueCalls);
    }

    [Fact]
    public async Task The_policy_is_asked_about_the_user_tenant_and_method()
    {
        var user = Staff();
        user.TenantId = TenantId.From("t1");
        users.UserToReturn = user;
        policy.Requirement = SecondFactorRequirement.NotRequired;

        await PasswordFlow().LoginAsync("staff", "pw");

        var seen = Assert.Single(policy.Contexts);
        Assert.Equal(new SecondFactorContext(user.Id, TenantId.From("t1"), SecondFactorMethod.Password), seen);
    }

    // ---- the seam in the one-time-code flow (a LoginAsync-only seam is bypassable through it)

    [Fact]
    public async Task One_time_code_login_that_owes_a_second_factor_issues_a_ticket_and_nothing_else()
    {
        var user = Staff();
        users.UserToReturn = user;
        policy.Requirement = SecondFactorRequirement.Verify;
        var challenges = new FakeOtpChallengeService
        {
            VerifyResult = new OtpVerifyResult(OtpVerifyOutcome.Verified, user.Id, NormalizedPhone) { TenantId = user.TenantId },
        };
        var flow = new OtpLoginFlow(users, new FakeOtpRecipientResolver(), challenges, new FakeOtpCodeSender(), new FormattingOnlyPhoneNumberNormalizer(),
            principals, access, refresh, hooks, [observer], options, clock, NullLogger<OtpLoginFlow>.Instance, policy, ticketService);

        var result = await flow.VerifyAsync(Guid.NewGuid(), "123456");

        Assert.Equal(LoginOutcome.SecondFactorRequired, result.Outcome);
        Assert.Equal(SecondFactorMethod.Otp, Assert.Single(policy.Contexts).Method);
        Assert.Equal(0, refresh.IssueCalls);
        Assert.DoesNotContain("login-succeeded", hooks.Calls);
        Assert.DoesNotContain("login-failed", hooks.Calls);
    }

    // ---- completion

    [Fact]
    public async Task Completing_a_ticket_issues_the_session_and_runs_the_success_hook_and_observer_once()
    {
        var pending = await PendingPasswordLoginAsync();

        var result = await Completion().CompleteAsync(pending.Ticket);

        Assert.True(result.Succeeded);
        Assert.Equal(SecondFactorMethod.Password, result.Method);
        Assert.Equal("access-jwt", result.Tokens!.Value.AccessToken);
        Assert.Equal(1, refresh.IssueCalls);
        Assert.Single(hooks.Calls, "login-succeeded");
        Assert.Single(observer.Calls, nameof(IIdentityEventObserver.OnLoginSucceededAsync));
    }

    [Fact]
    public async Task A_ticket_is_single_use()
    {
        var pending = await PendingPasswordLoginAsync();
        var completion = Completion();

        Assert.True((await completion.CompleteAsync(pending.Ticket)).Succeeded);
        var second = await completion.CompleteAsync(pending.Ticket);

        Assert.Equal(SecondFactorCompletionOutcome.Refused, second.Outcome);
        Assert.Equal(1, refresh.IssueCalls);
    }

    [Fact]
    public async Task A_tampered_or_foreign_ticket_is_refused()
    {
        var pending = await PendingPasswordLoginAsync();
        var tampered = pending.Ticket[..^3] + (pending.Ticket.EndsWith("AAA", StringComparison.Ordinal) ? "BBB" : "AAA");

        Assert.Equal(SecondFactorCompletionOutcome.Refused, (await Completion().CompleteAsync(tampered)).Outcome);
        Assert.Equal(SecondFactorCompletionOutcome.Refused, (await Completion().CompleteAsync("not-a-ticket")).Outcome);
        Assert.Equal(0, refresh.IssueCalls);
    }

    [Fact]
    public async Task An_expired_ticket_is_refused()
    {
        var pending = await PendingPasswordLoginAsync();

        clock.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(SecondFactorCompletionOutcome.Refused, (await Completion().CompleteAsync(pending.Ticket)).Outcome);
    }

    [Fact]
    public async Task An_enrol_ticket_lives_longer_than_a_verify_ticket()
    {
        users.UserToReturn = Staff();
        policy.Requirement = SecondFactorRequirement.Enrol;
        var pending = (await PasswordFlow().LoginAsync("staff", "pw")).SecondFactor!.Value;

        clock.Advance(TimeSpan.FromMinutes(10));

        Assert.True((await Completion().CompleteAsync(pending.Ticket)).Succeeded);
    }

    [Fact]
    public async Task A_changed_security_stamp_voids_the_ticket()
    {
        var user = Staff();
        var pending = await PendingPasswordLoginAsync(user);

        user.SecurityStamp = Guid.NewGuid().ToString("N");

        Assert.Equal(SecondFactorCompletionOutcome.Refused, (await Completion().CompleteAsync(pending.Ticket)).Outcome);
        Assert.Equal(0, refresh.IssueCalls);
    }

    [Fact]
    public async Task A_user_deactivated_or_locked_since_the_primary_check_is_refused()
    {
        var user = Staff();
        var pending = await PendingPasswordLoginAsync(user);
        user.IsActive = false;
        Assert.Equal(SecondFactorCompletionOutcome.Refused, (await Completion().CompleteAsync(pending.Ticket)).Outcome);

        user.IsActive = true;
        var second = await PendingPasswordLoginAsync(user);
        user.LockoutEnd = clock.GetUtcNow().AddHours(1);
        Assert.Equal(SecondFactorCompletionOutcome.Refused, (await Completion().CompleteAsync(second.Ticket)).Outcome);
        Assert.Equal(0, refresh.IssueCalls);
    }

    [Fact]
    public async Task A_login_method_switched_off_since_the_ticket_was_issued_is_refused()
    {
        var pending = await PendingPasswordLoginAsync();

        options.EnabledLoginMethods = LoginMethods.Otp | LoginMethods.External;

        Assert.Equal(SecondFactorCompletionOutcome.Refused, (await Completion().CompleteAsync(pending.Ticket)).Outcome);
    }

    [Fact]
    public async Task A_hook_that_denies_at_completion_returns_denied_and_issues_nothing()
    {
        var pending = await PendingPasswordLoginAsync();
        hooks.DenyOnSucceeded = true;

        var result = await Completion().CompleteAsync(pending.Ticket);

        Assert.Equal(SecondFactorCompletionOutcome.Denied, result.Outcome);
        Assert.Equal(0, refresh.IssueCalls);
        Assert.Contains(nameof(IIdentityEventObserver.OnLoginDeniedAsync), observer.Calls);
        // The ticket is burnt either way.
        hooks.DenyOnSucceeded = false;
        Assert.Equal(SecondFactorCompletionOutcome.Refused, (await Completion().CompleteAsync(pending.Ticket)).Outcome);
    }

    [Fact]
    public async Task Completion_runs_in_the_tickets_tenant_scope_when_the_request_has_none()
    {
        var user = Staff();
        user.TenantId = TenantId.From("t1");
        var pending = await PendingPasswordLoginAsync(user);
        TenantContextAccessor.CurrentTenantId = null;

        var result = await Completion().CompleteAsync(pending.Ticket);

        Assert.True(result.Succeeded);
        Assert.Equal(TenantId.From("t1"), principals.TenantSeen);
        Assert.Equal(TenantId.From("t1"), users.TenantSeenOnFindById);
    }

    [Fact]
    public async Task A_ticket_of_a_tenant_user_is_refused_when_the_user_now_belongs_elsewhere()
    {
        var user = Staff();
        user.TenantId = TenantId.From("t1");
        var pending = await PendingPasswordLoginAsync(user);

        user.TenantId = TenantId.From("t2");

        Assert.Equal(SecondFactorCompletionOutcome.Refused, (await Completion().CompleteAsync(pending.Ticket)).Outcome);
    }

    [Fact]
    public async Task Reject_burns_a_ticket_without_completing()
    {
        var pending = await PendingPasswordLoginAsync();
        var completion = Completion();

        await completion.RejectAsync(pending.Ticket);

        Assert.Equal(SecondFactorCompletionOutcome.Refused, (await completion.CompleteAsync(pending.Ticket)).Outcome);
        Assert.Equal(0, refresh.IssueCalls);
    }

    [Fact]
    public async Task An_external_ticket_fires_the_external_hook_and_observer_and_never_the_password_ones()
    {
        var user = Staff();
        users.UserToReturn = user;
        var pending = await ticketService.IssueAsync(new SecondFactorTicketData(
            user.Id, user.TenantId, SecondFactorMethod.External, "line", WasCreated: true, WasLinked: true, user.SecurityStamp, SecondFactorStage.Verify));

        var result = await Completion().CompleteAsync(pending.Ticket);

        Assert.True(result.Succeeded);
        Assert.Equal(SecondFactorMethod.External, result.Method);
        Assert.Equal("line", result.Provider);
        Assert.True(result.WasCreated);
        Assert.True(result.WasLinked);
        Assert.Equal(1, externalHooks.Succeeded);
        Assert.DoesNotContain("login-succeeded", hooks.Calls);
        Assert.Contains(nameof(IIdentityEventObserver.OnExternalLoginSucceededAsync), observer.Calls);
        Assert.DoesNotContain(nameof(IIdentityEventObserver.OnLoginSucceededAsync), observer.Calls);
    }

    [Fact]
    public async Task An_external_ticket_is_refused_when_external_login_is_not_registered()
    {
        var user = Staff();
        users.UserToReturn = user;
        var pending = await ticketService.IssueAsync(new SecondFactorTicketData(
            user.Id, user.TenantId, SecondFactorMethod.External, "line", false, false, user.SecurityStamp, SecondFactorStage.Verify));
        var completion = new SecondFactorCompletion(ticketService, users, principals, access, refresh, hooks, [observer], options, clock,
            NullLogger<SecondFactorCompletion>.Instance);

        Assert.Equal(SecondFactorCompletionOutcome.Refused, (await completion.CompleteAsync(pending.Ticket)).Outcome);
        Assert.Equal(0, refresh.IssueCalls);
    }

    [Fact]
    public async Task A_one_time_code_ticket_is_refused_when_the_phone_is_no_longer_confirmed()
    {
        var user = Staff();
        users.UserToReturn = user;
        var pending = await ticketService.IssueAsync(new SecondFactorTicketData(
            user.Id, user.TenantId, SecondFactorMethod.Otp, null, false, false, user.SecurityStamp, SecondFactorStage.Verify));

        user.PhoneNumberConfirmed = false;

        Assert.Equal(SecondFactorCompletionOutcome.Refused, (await Completion().CompleteAsync(pending.Ticket)).Outcome);
    }

    [Fact]
    public async Task No_ticket_secret_reaches_the_logs()
    {
        var logger = new CapturingLogger<SecondFactorCompletion>();
        var pending = await PendingPasswordLoginAsync();
        var completion = new SecondFactorCompletion(ticketService, users, principals, access, refresh, hooks, [observer], options, clock, logger, externalHooks);

        await completion.CompleteAsync(pending.Ticket);
        await completion.CompleteAsync(pending.Ticket);

        Assert.DoesNotContain(pending.Ticket, logger.Text);
    }

    // ---- the ticket service on its own

    [Fact]
    public async Task A_ticket_protected_by_another_key_ring_is_refused()
    {
        var pending = await PendingPasswordLoginAsync();
        var other = new SecondFactorTicketService(new EphemeralDataProtectionProvider(), TokenServices(userTokens), clock);

        Assert.Null(await other.ConsumeAsync(pending.Ticket));
    }
}
