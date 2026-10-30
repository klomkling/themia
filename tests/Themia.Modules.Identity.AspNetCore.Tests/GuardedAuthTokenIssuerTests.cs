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
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.Tests;

public sealed class GuardedAuthTokenIssuerTests : IDisposable
{
    private const string Channel = "federation";

    private readonly TenantId? originalTenant = TenantContextAccessor.CurrentTenantId;
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-10-31T10:00:00Z"));
    private readonly FakeUserService users = new();
    private readonly FakeRefreshTokenService refresh = new();
    private readonly FakeAccessTokenService access;
    private readonly FakeClaimsPrincipalFactory principals = new();
    private readonly RecordingObserver observer = new();
    private readonly ScriptedSecondFactorPolicy policy = new();
    private readonly InMemoryUserTokenService userTokens;
    private readonly SecondFactorTicketService ticketService;
    private readonly IdentityModuleOptions options = new();

    public GuardedAuthTokenIssuerTests()
    {
        access = new FakeAccessTokenService(clock);
        userTokens = new InMemoryUserTokenService(clock);
        ticketService = new SecondFactorTicketService(
            new EphemeralDataProtectionProvider(), new ServiceCollection().AddSingleton<IUserTokenService>(userTokens).BuildServiceProvider(), clock);
    }

    public void Dispose() => TenantContextAccessor.CurrentTenantId = originalTenant;

    private GuardedAuthTokenIssuer Issuer(ISecondFactorTicketService? tickets = null) =>
        new(users, principals, access, refresh, policy, [observer], options, clock, NullLogger<GuardedAuthTokenIssuer>.Instance, tickets ?? ticketService);

    private static User Member(string? tenant = "t1") => new() { UserName = "m", IsActive = true, TenantId = tenant is null ? null : TenantId.From(tenant) };

    private static SessionRequest Request(string? tenant = "t1", IReadOnlyCollection<Claim>? claims = null, string channel = Channel) =>
        new(tenant is null ? null : TenantId.From(tenant), channel, claims);

    // ---- start

    [Fact]
    public async Task Start_issues_refresh_then_access_with_sid_and_raises_the_observer()
    {
        var user = Member();
        users.UserToReturn = user;

        var result = await Issuer().StartSessionAsync(user.Id, Request());

        Assert.True(result.Succeeded);
        Assert.Equal(refresh.LastFamilyId, result.SessionId);
        Assert.Equal("refresh-raw", result.Tokens!.Value.RefreshToken);
        Assert.Equal(refresh.LastFamilyId.ToString(), access.LastPrincipal!.FindFirst("sid")!.Value);
        Assert.Equal((user.Id, Channel, refresh.LastFamilyId), Assert.Single(observer.SessionsIssued));
    }

    [Fact]
    public async Task Start_runs_the_user_lookup_in_the_expected_tenant_scope()
    {
        var user = Member();
        users.UserToReturn = user;
        TenantContextAccessor.CurrentTenantId = null;

        await Issuer().StartSessionAsync(user.Id, Request());

        Assert.Equal(TenantId.From("t1"), users.TenantSeenOnFindById);
        Assert.Equal(TenantId.From("t1"), principals.TenantSeen);
    }

    [Fact]
    public async Task Start_reloads_the_user_so_a_lock_after_the_app_loaded_it_is_seen()
    {
        var user = Member();
        users.UserToReturn = user;
        user.LockoutEnd = clock.GetUtcNow().AddHours(1);

        var result = await Issuer().StartSessionAsync(user.Id, Request());

        Assert.Equal(IssueOutcome.LockedOut, result.Outcome);
        AssertNothingIssued();
    }

    [Fact]
    public async Task Start_refuses_an_inactive_user_with_nothing_issued()
    {
        var user = Member();
        user.IsActive = false;
        users.UserToReturn = user;

        Assert.Equal(IssueOutcome.Inactive, (await Issuer().StartSessionAsync(user.Id, Request())).Outcome);
        AssertNothingIssued();
    }

    [Theory]
    [InlineData("t2")]
    [InlineData(null)]
    public async Task Start_refuses_a_user_of_another_tenant_or_a_platform_user_in_a_tenant_scope(string? userTenant)
    {
        var user = Member(userTenant);
        users.UserToReturn = user;

        Assert.Equal(IssueOutcome.TenantMismatch, (await Issuer().StartSessionAsync(user.Id, Request("t1"))).Outcome);
        AssertNothingIssued();
    }

    [Fact]
    public async Task Start_refuses_an_unknown_user_the_same_way()
    {
        users.UserToReturn = null;

        Assert.Equal(IssueOutcome.TenantMismatch, (await Issuer().StartSessionAsync(Guid.NewGuid(), Request())).Outcome);
        AssertNothingIssued();
    }

    [Fact]
    public async Task Start_refuses_a_platform_user_when_platform_login_is_off()
    {
        var user = Member(null);
        users.UserToReturn = user;
        options.AllowPlatformLogin = false;

        Assert.Equal(IssueOutcome.TenantMismatch, (await Issuer().StartSessionAsync(user.Id, Request(null))).Outcome);
        AssertNothingIssued();
    }

    // ---- second factor

    [Theory]
    [InlineData(SecondFactorRequirement.Verify, SecondFactorStage.Verify)]
    [InlineData(SecondFactorRequirement.Enrol, SecondFactorStage.Enrol)]
    public async Task Start_returns_a_custom_ticket_when_the_policy_wants_a_factor(SecondFactorRequirement requirement, SecondFactorStage stage)
    {
        var user = Member();
        users.UserToReturn = user;
        policy.Requirement = requirement;

        var result = await Issuer().StartSessionAsync(user.Id, Request());

        Assert.Equal(IssueOutcome.SecondFactorRequired, result.Outcome);
        Assert.Equal(stage, result.SecondFactor!.Value.Stage);
        Assert.Equal(new SecondFactorContext(user.Id, TenantId.From("t1"), SecondFactorMethod.Custom, Channel), Assert.Single(policy.Contexts));
        AssertNothingIssued();
    }

    [Fact]
    public async Task A_policy_that_throws_stops_the_start_with_nothing_issued()
    {
        var user = Member();
        users.UserToReturn = user;
        policy.Throw = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Issuer().StartSessionAsync(user.Id, Request()));
        AssertNothingIssued();
    }

    [Fact]
    public async Task Complete_issues_the_session_with_the_app_claims_and_no_login_hook()
    {
        var user = Member();
        users.UserToReturn = user;
        policy.Requirement = SecondFactorRequirement.Verify;
        var claims = new[] { new Claim("app:session_start", "123") };
        var pending = (await Issuer().StartSessionAsync(user.Id, Request(claims: claims))).SecondFactor!.Value;

        var result = await Issuer().CompleteSessionAsync(user.Id, pending.Ticket, pending.Stage, Request(claims: claims));

        Assert.True(result.Succeeded);
        Assert.Equal("123", access.LastPrincipal!.FindFirst("app:session_start")!.Value);
        Assert.Equal(refresh.LastFamilyId.ToString(), access.LastPrincipal.FindFirst("sid")!.Value);
        Assert.Single(observer.SessionsIssued);
        Assert.Equal(1, policy.Calls);
    }

    [Fact]
    public async Task Complete_refuses_another_users_ticket_a_replay_a_wrong_channel_and_a_changed_stamp()
    {
        var user = Member();
        users.UserToReturn = user;
        policy.Requirement = SecondFactorRequirement.Verify;
        var issuer = Issuer();

        var forOther = (await issuer.StartSessionAsync(user.Id, Request())).SecondFactor!.Value;
        Assert.Equal(IssueOutcome.TicketInvalid, (await issuer.CompleteSessionAsync(Guid.NewGuid(), forOther.Ticket, forOther.Stage, Request())).Outcome);

        var wrongChannel = (await issuer.StartSessionAsync(user.Id, Request())).SecondFactor!.Value;
        Assert.Equal(IssueOutcome.TicketInvalid, (await issuer.CompleteSessionAsync(user.Id, wrongChannel.Ticket, wrongChannel.Stage, Request(channel: "chat"))).Outcome);

        var once = (await issuer.StartSessionAsync(user.Id, Request())).SecondFactor!.Value;
        Assert.True((await issuer.CompleteSessionAsync(user.Id, once.Ticket, once.Stage, Request())).Succeeded);
        Assert.Equal(IssueOutcome.TicketInvalid, (await issuer.CompleteSessionAsync(user.Id, once.Ticket, once.Stage, Request())).Outcome);

        var stale = (await issuer.StartSessionAsync(user.Id, Request())).SecondFactor!.Value;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        Assert.Equal(IssueOutcome.TicketInvalid, (await issuer.CompleteSessionAsync(user.Id, stale.Ticket, stale.Stage, Request())).Outcome);
        Assert.Equal(1, access.IssueCalls);
    }

    [Fact]
    public async Task Complete_refuses_a_verify_ticket_presented_at_the_enrol_stage_and_burns_it()
    {
        var user = Member();
        users.UserToReturn = user;
        policy.Requirement = SecondFactorRequirement.Verify;
        var issuer = Issuer();
        var pending = (await issuer.StartSessionAsync(user.Id, Request())).SecondFactor!.Value;

        Assert.Equal(IssueOutcome.TicketInvalid, (await issuer.CompleteSessionAsync(user.Id, pending.Ticket, SecondFactorStage.Enrol, Request())).Outcome);
        Assert.Equal(IssueOutcome.TicketInvalid, (await issuer.CompleteSessionAsync(user.Id, pending.Ticket, pending.Stage, Request())).Outcome);
        AssertNothingIssued();
    }

    [Fact]
    public async Task Complete_refuses_a_ticket_of_another_method()
    {
        var user = Member();
        users.UserToReturn = user;
        var password = await ticketService.IssueAsync(new SecondFactorTicketData(
            user.Id, user.TenantId, SecondFactorMethod.Password, null, false, false, user.SecurityStamp, SecondFactorStage.Verify));

        Assert.Equal(IssueOutcome.TicketInvalid, (await Issuer().CompleteSessionAsync(user.Id, password.Ticket, password.Stage, Request())).Outcome);
        AssertNothingIssued();
    }

    [Fact]
    public async Task The_completion_service_refuses_a_custom_ticket()
    {
        var user = Member();
        users.UserToReturn = user;
        policy.Requirement = SecondFactorRequirement.Verify;
        var pending = (await Issuer().StartSessionAsync(user.Id, Request())).SecondFactor!.Value;
        var completion = new SecondFactorCompletion(ticketService, users, principals, access, refresh, new RecordingHooks(), [observer], options, clock,
            NullLogger<SecondFactorCompletion>.Instance);

        var result = await completion.CompleteAsync(pending.Ticket, pending.UserId, pending.Stage);

        Assert.Equal(SecondFactorCompletionOutcome.Refused, result.Outcome);
        Assert.Equal(0, refresh.IssueCalls);
    }

    // ---- claims, channel, compensation

    [Theory]
    [InlineData("sid")]
    [InlineData("SID")]
    [InlineData("themia:anything")]
    [InlineData("sub")]
    [InlineData("role")]
    public async Task A_reserved_app_claim_throws_before_anything_is_issued(string type)
    {
        var user = Member();
        users.UserToReturn = user;

        await Assert.ThrowsAsync<ArgumentException>(() => Issuer().StartSessionAsync(user.Id, Request(claims: [new Claim(type, "x")])));
        AssertNothingIssued();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Federation")]
    [InlineData("a b")]
    [InlineData("this-channel-name-is-far-too-long-to-be-accepted-by-the-issuer-because-it-exceeds-sixty-four")]
    public async Task A_bad_channel_throws(string channel)
    {
        users.UserToReturn = Member();

        await Assert.ThrowsAsync<ArgumentException>(() => Issuer().StartSessionAsync(Guid.NewGuid(), Request(channel: channel)));
    }

    [Fact]
    public async Task An_app_claim_replaces_a_claim_of_the_same_type_instead_of_duplicating_it()
    {
        var user = Member();
        users.UserToReturn = user;
        principals.ExtraClaims.Add(new Claim("app:session_start", "old"));

        await Issuer().StartSessionAsync(user.Id, Request(claims: [new Claim("app:session_start", "new")]));

        Assert.Equal("new", Assert.Single(access.LastPrincipal!.FindAll("app:session_start")).Value);
    }

    [Fact]
    public async Task When_the_access_token_cannot_be_issued_the_refresh_row_is_revoked_even_if_the_caller_cancelled_and_the_original_error_surfaces()
    {
        var user = Member();
        users.UserToReturn = user;
        access.ThrowOnIssue = true;
        refresh.ThrowOnRevoke = true;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Issuer().StartSessionAsync(user.Id, Request(), cts.Token));

        Assert.Equal("signing failed", error.Message);
        Assert.Equal(1, refresh.RevokeCalls);
        Assert.False(refresh.LastRevokeToken.IsCancellationRequested);
        Assert.Empty(observer.SessionsIssued);
    }

    [Fact]
    public async Task A_throwing_observer_does_not_fail_an_issued_session()
    {
        var user = Member();
        users.UserToReturn = user;
        observer.ThrowOnSessionIssued = true;

        Assert.True((await Issuer().StartSessionAsync(user.Id, Request())).Succeeded);
    }

    // ---- renewal

    [Fact]
    public async Task Renew_issues_an_access_token_only_for_a_live_session_without_asking_the_policy_or_the_observer()
    {
        var user = Member();
        users.UserToReturn = user;
        var session = Guid.NewGuid();
        refresh.LiveSessions.Add((user.Id, session));
        policy.Requirement = SecondFactorRequirement.Verify;

        var result = await Issuer().RenewSessionAsync(user.Id, session, Request());

        Assert.True(result.Succeeded);
        Assert.Equal(session, result.SessionId);
        Assert.Equal(string.Empty, result.Tokens!.Value.RefreshToken);
        Assert.Equal(session.ToString(), access.LastPrincipal!.FindFirst("sid")!.Value);
        Assert.Equal(0, refresh.IssueCalls);
        Assert.Equal(0, policy.Calls);
        Assert.Empty(observer.SessionsIssued);
    }

    [Fact]
    public async Task Renew_refuses_a_session_that_is_not_a_live_session_of_this_user()
    {
        var user = Member();
        users.UserToReturn = user;
        var someoneElses = Guid.NewGuid();
        refresh.LiveSessions.Add((Guid.NewGuid(), someoneElses));

        Assert.Equal(IssueOutcome.SessionNotLive, (await Issuer().RenewSessionAsync(user.Id, someoneElses, Request())).Outcome);
        Assert.Equal(IssueOutcome.SessionNotLive, (await Issuer().RenewSessionAsync(user.Id, Guid.NewGuid(), Request())).Outcome);
        AssertNothingIssued();
    }

    [Fact]
    public async Task Renew_still_applies_the_guards_to_a_live_session()
    {
        var user = Member();
        users.UserToReturn = user;
        var session = Guid.NewGuid();
        refresh.LiveSessions.Add((user.Id, session));
        user.IsActive = false;

        Assert.Equal(IssueOutcome.Inactive, (await Issuer().RenewSessionAsync(user.Id, session, Request())).Outcome);
        AssertNothingIssued();
    }

    private void AssertNothingIssued()
    {
        Assert.Equal(0, refresh.IssueCalls);
        Assert.Equal(0, access.IssueCalls);
        Assert.Empty(observer.SessionsIssued);
    }
}
