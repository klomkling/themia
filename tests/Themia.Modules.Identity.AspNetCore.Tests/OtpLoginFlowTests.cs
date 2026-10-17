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

public sealed class OtpLoginFlowTests : IDisposable
{
    private const string RawPhone = "081-234-5678";
    private const string NormalizedPhone = "0812345678";
    private const string Code = "123456";

    private readonly TenantId? originalTenant = TenantContextAccessor.CurrentTenantId;
    private readonly FakeOtpRecipientResolver resolver = new();
    private readonly IdentityModuleOptions identityOptions = new();
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-10-17T10:00:00Z"));
    private readonly FakeUserService users = new();
    private readonly FakeOtpChallengeService challenges = new();
    private readonly FakeOtpCodeSender sender = new();
    private readonly FakeRefreshTokenService refresh = new();
    private readonly RecordingHooks hooks = new();
    private readonly RecordingObserver observer = new();
    private readonly FakeAccessTokenService access;
    private readonly FakeClaimsPrincipalFactory principals = new();
    private readonly OtpLoginFlow flow;

    public void Dispose() => TenantContextAccessor.CurrentTenantId = originalTenant;

    public OtpLoginFlowTests()
    {
        access = new FakeAccessTokenService(clock);
        hooks.Refresh = refresh;
        flow = new OtpLoginFlow(users, resolver, challenges, sender, new FormattingOnlyPhoneNumberNormalizer(),
            principals, access, refresh, hooks, [observer], identityOptions, clock, NullLogger<OtpLoginFlow>.Instance);
    }

    private static User Eligible() => new()
    {
        UserName = "alice", IsActive = true, PhoneNumber = RawPhone, NormalizedPhoneNumber = NormalizedPhone, PhoneNumberConfirmed = true,
    };

    private void GivenAccount(User? user = null)
    {
        var u = user ?? Eligible();
        resolver.Recipient = new OtpRecipient(u.Id, u.TenantId, RawPhone);
        users.UserToReturn = u;
        challenges.VerifyResult = new OtpVerifyResult(OtpVerifyOutcome.Verified, u.Id, NormalizedPhone) { TenantId = u.TenantId };
    }

    [Fact]
    public async Task Start_DeliversTheCodeForAConfirmedActivePhone()
    {
        var user = Eligible();
        GivenAccount(user);

        var result = await flow.StartAsync(RawPhone);

        Assert.True(result.Issued);
        Assert.Equal(NormalizedPhone, challenges.LastIssuePhone);
        Assert.Equal(user.Id, challenges.LastIssueUserId);
        var delivery = Assert.Single(sender.Sent);
        Assert.Equal(Code, delivery.Code);
        Assert.Equal(result.ChallengeId, delivery.ChallengeId);
    }

    [Fact]
    public async Task Start_ForAnUnknownPhoneReturnsTheSameShapeAndSendsNothing()
    {
        var result = await flow.StartAsync(RawPhone);

        Assert.True(result.Issued);
        Assert.NotEqual(Guid.Empty, result.ChallengeId);
        Assert.Null(challenges.LastIssueUserId);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Start_IssuesTheChallengeAsTheRecipientsTenant_AndRestoresTheCallersScope()
    {
        var user = Eligible();
        user.TenantId = new TenantId("acme");
        GivenAccount(user);
        TenantContextAccessor.CurrentTenantId = null;

        await flow.StartAsync(RawPhone);

        Assert.Equal(new TenantId("acme"), challenges.TenantSeenOnIssue);
        Assert.Null(TenantContextAccessor.CurrentTenantId);
    }

    [Fact]
    public async Task Start_ForADecoyKeepsTheCallersScope()
    {
        TenantContextAccessor.CurrentTenantId = new TenantId("beta");

        await flow.StartAsync(RawPhone);

        Assert.Equal(new TenantId("beta"), challenges.TenantSeenOnIssue);
        Assert.Equal(new TenantId("beta"), TenantContextAccessor.CurrentTenantId);
    }

    [Fact]
    public async Task Start_ReportsThrottledWithoutSending()
    {
        GivenAccount();
        challenges.IssueOutcome = OtpIssueOutcome.Throttled;

        var result = await flow.StartAsync(RawPhone);

        Assert.Equal(OtpStartOutcome.Throttled, result.Outcome);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Start_StillReturnsIssuedWhenTheSenderFails()
    {
        GivenAccount();
        sender.Throw = true;

        var result = await flow.StartAsync(RawPhone);

        Assert.True(result.Issued);
    }

    [Fact]
    public async Task Start_NeverLogsTheCodeOrTheSenderExceptionMessage()
    {
        GivenAccount();
        sender.Throw = true;
        var log = new ListLogger<OtpLoginFlow>();
        var logging = new OtpLoginFlow(users, resolver, challenges, sender, new FormattingOnlyPhoneNumberNormalizer(),
            new FakeClaimsPrincipalFactory(), access, refresh, hooks, [], identityOptions, clock, log);

        await logging.StartAsync(RawPhone);

        Assert.NotEmpty(log.Entries);
        Assert.All(log.Entries, e =>
        {
            Assert.Null(e.Exception);
            Assert.DoesNotContain(Code, e.Message);
            Assert.DoesNotContain("0812345678", e.Message);
        });
    }

    [Fact]
    public async Task Start_PropagatesCancellationFromTheSender()
    {
        GivenAccount();
        sender.CancelOnSend = true;

        await Assert.ThrowsAsync<OperationCanceledException>(() => flow.StartAsync(RawPhone));
    }

    [Fact]
    public async Task Verify_IssuesTheSameTokensAsPasswordLoginAndRunsTheHooks()
    {
        GivenAccount();

        var result = await flow.VerifyAsync(Guid.NewGuid(), Code);

        Assert.True(result.Succeeded);
        Assert.Equal("access-jwt", result.Tokens!.Value.AccessToken);
        Assert.Equal("refresh-raw", result.Tokens!.Value.RefreshToken);
        Assert.Equal(1, refresh.IssueCalls);
        Assert.Equal(["before-login", "login-succeeded"], hooks.Calls);
        Assert.True(hooks.SucceededRanBeforeIssue);
        Assert.Contains(nameof(IIdentityEventObserver.OnLoginSucceededAsync), observer.Calls);
    }

    [Theory]
    [InlineData(OtpVerifyOutcome.Invalid, LoginOutcome.InvalidCredentials)]
    [InlineData(OtpVerifyOutcome.LockedOut, LoginOutcome.LockedOut)]
    public async Task Verify_MapsAFailedCheckWithoutIssuingTokens(OtpVerifyOutcome check, LoginOutcome expected)
    {
        GivenAccount();
        challenges.VerifyResult = new OtpVerifyResult(check, null, null);

        var result = await flow.VerifyAsync(Guid.NewGuid(), Code);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(0, refresh.IssueCalls);
        Assert.Equal(0, access.IssueCalls);
        // The attempt cap is its own reason: LockedOut means the ACCOUNT is locked, and an audit reader must not confuse the two.
        Assert.Equal(check == OtpVerifyOutcome.LockedOut ? LoginFailureReason.OtpAttemptsExceeded : LoginFailureReason.InvalidOtp, hooks.FailedReason);
        Assert.Contains(nameof(IIdentityEventObserver.OnLoginFailedAsync), observer.Calls);
    }

    [Fact]
    public async Task Verify_ReChecksTheAccountAfterTheCode_InactiveGetsNoTokens()
    {
        var user = Eligible();
        GivenAccount(user);
        user.IsActive = false;

        var result = await flow.VerifyAsync(Guid.NewGuid(), Code);

        Assert.False(result.Succeeded);
        Assert.Equal(0, refresh.IssueCalls);
        Assert.Equal(LoginFailureReason.Inactive, hooks.FailedReason);
    }

    [Fact]
    public async Task Verify_ReChecksTheAccountAfterTheCode_LockedOutGetsNoTokens()
    {
        var user = Eligible();
        GivenAccount(user);
        user.LockoutEnd = clock.GetUtcNow().AddMinutes(5);

        var result = await flow.VerifyAsync(Guid.NewGuid(), Code);

        Assert.Equal(LoginOutcome.LockedOut, result.Outcome);
        Assert.Equal(0, refresh.IssueCalls);
        Assert.Equal(LoginFailureReason.LockedOut, hooks.FailedReason);
    }

    [Fact]
    public async Task Verify_ReChecksTheAccountAfterTheCode_UnconfirmedPhoneGetsNoTokens()
    {
        var user = Eligible();
        GivenAccount(user);
        user.PhoneNumberConfirmed = false;

        var result = await flow.VerifyAsync(Guid.NewGuid(), Code);

        Assert.False(result.Succeeded);
        Assert.Equal(0, refresh.IssueCalls);
    }

    [Fact]
    public async Task Verify_GetsNoTokensWhenTheUserChangedPhoneAfterTheCodeWasSent()
    {
        var user = Eligible();
        GivenAccount(user);
        user.PhoneNumber = "0899999999";
        user.NormalizedPhoneNumber = "0899999999";

        var result = await flow.VerifyAsync(Guid.NewGuid(), Code);

        Assert.False(result.Succeeded);
        Assert.Equal(0, refresh.IssueCalls);
        Assert.Equal(LoginFailureReason.InvalidOtp, hooks.FailedReason);
    }

    [Fact]
    public async Task Verify_GetsNoTokensWhenTheUserLostThePhone()
    {
        var user = Eligible();
        GivenAccount(user);
        user.PhoneNumber = null;
        user.NormalizedPhoneNumber = null;

        Assert.False((await flow.VerifyAsync(Guid.NewGuid(), Code)).Succeeded);
        Assert.Equal(0, refresh.IssueCalls);
    }

    [Fact]
    public async Task Verify_RunsTheUserLookupPrincipalAndHooksAsTheChallengesTenant_ThenRestoresTheScope()
    {
        var user = Eligible();
        user.TenantId = new TenantId("acme");
        GivenAccount(user);
        TenantContextAccessor.CurrentTenantId = null;
        var seen = new List<TenantId?>();
        var probing = new OtpLoginFlow(users, resolver, challenges, sender, new FormattingOnlyPhoneNumberNormalizer(),
            principals, access, refresh, new CapturingBeforeLoginHooks(_ => seen.Add(TenantContextAccessor.CurrentTenantId)), [],
            identityOptions, clock, NullLogger<OtpLoginFlow>.Instance);

        var result = await probing.VerifyAsync(Guid.NewGuid(), Code);

        Assert.True(result.Succeeded);
        Assert.Equal(new TenantId("acme"), Assert.Single(seen));
        Assert.Equal(new TenantId("acme"), users.TenantSeenOnFindById);
        Assert.Equal(new TenantId("acme"), principals.TenantSeen);
        Assert.Null(TenantContextAccessor.CurrentTenantId);
    }

    [Fact]
    public async Task Verify_GetsNoTokensWhenTheUserBelongsToAnotherTenantThanTheChallenge()
    {
        var user = Eligible();
        user.TenantId = new TenantId("beta");
        GivenAccount(user);
        challenges.VerifyResult = new OtpVerifyResult(OtpVerifyOutcome.Verified, user.Id, NormalizedPhone) { TenantId = new TenantId("acme") };

        var result = await flow.VerifyAsync(Guid.NewGuid(), Code);

        Assert.False(result.Succeeded);
        Assert.Equal(0, refresh.IssueCalls);
        Assert.False(principals.Called);
        Assert.Equal(LoginFailureReason.InvalidOtp, hooks.FailedReason);
    }

    [Fact]
    public async Task Verify_RestoresTheCallersScopeWhenAHookThrows()
    {
        var user = Eligible();
        user.TenantId = new TenantId("acme");
        GivenAccount(user);
        TenantContextAccessor.CurrentTenantId = new TenantId("beta");
        var throwing = new OtpLoginFlow(users, resolver, challenges, sender, new FormattingOnlyPhoneNumberNormalizer(),
            principals, access, refresh, new ThrowingBeforeLoginHooks(), [], identityOptions, clock, NullLogger<OtpLoginFlow>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => throwing.VerifyAsync(Guid.NewGuid(), Code));

        Assert.Equal(new TenantId("beta"), TenantContextAccessor.CurrentTenantId);
    }

    [Fact]
    public async Task Verify_RefusesAPlatformUser_WhenPlatformLoginIsOff_EvenForAChallengeIssuedBeforeItWasTurnedOff()
    {
        var platformUser = Eligible();
        platformUser.TenantId = null;
        GivenAccount(platformUser);
        identityOptions.AllowPlatformLogin = false;

        var result = await flow.VerifyAsync(Guid.NewGuid(), Code);

        Assert.False(result.Succeeded);
        Assert.Equal(0, refresh.IssueCalls);
        Assert.False(principals.Called);
    }

    [Fact]
    public async Task Verify_AcceptsAPlatformUser_WhenPlatformLoginIsOn()
    {
        var platformUser = Eligible();
        platformUser.TenantId = null;
        GivenAccount(platformUser);
        identityOptions.AllowPlatformLogin = true;

        Assert.True((await flow.VerifyAsync(Guid.NewGuid(), Code)).Succeeded);
    }

    [Fact]
    public async Task Verify_GetsNoTokensWhenTheUserIsGone()
    {
        GivenAccount();
        users.UserToReturn = null;

        var result = await flow.VerifyAsync(Guid.NewGuid(), Code);

        Assert.False(result.Succeeded);
        Assert.Equal(0, refresh.IssueCalls);
    }

    [Fact]
    public async Task Verify_LetsTheBeforeLoginHookVetoTheLogin()
    {
        GivenAccount();
        hooks.DenyBeforeLogin = true;

        var result = await flow.VerifyAsync(Guid.NewGuid(), Code);

        Assert.Equal(LoginOutcome.Denied, result.Outcome);
        Assert.Equal(0, refresh.IssueCalls);
        Assert.Contains(nameof(IIdentityEventObserver.OnLoginDeniedAsync), observer.Calls);
    }

    [Fact]
    public async Task Verify_LetsTheLoginSucceededHookVetoBeforeAnyTokenIsIssued()
    {
        GivenAccount();
        hooks.DenyOnSucceeded = true;

        var result = await flow.VerifyAsync(Guid.NewGuid(), Code);

        Assert.Equal(LoginOutcome.Denied, result.Outcome);
        Assert.Equal(0, refresh.IssueCalls);
    }

    [Fact]
    public async Task Verify_RunsTheBeforeLoginHookWithThePhoneTheChallengeWasIssuedFor()
    {
        GivenAccount();
        string? seen = null;
        var capturing = new CapturingBeforeLoginHooks(identifier => seen = identifier);
        var capturingFlow = new OtpLoginFlow(users, resolver, challenges, sender, new FormattingOnlyPhoneNumberNormalizer(),
            new FakeClaimsPrincipalFactory(), access, refresh, capturing, [], identityOptions, clock, NullLogger<OtpLoginFlow>.Instance);

        await capturingFlow.VerifyAsync(Guid.NewGuid(), Code);

        Assert.Equal(NormalizedPhone, seen);
    }

    [Fact]
    public async Task Start_RejectsABlankPhone()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => flow.StartAsync("  "));
    }

    private sealed class ThrowingBeforeLoginHooks : AuthenticationHooksBase
    {
        public override Task OnBeforeLoginAsync(BeforeLoginContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("hook failed");
    }

    private sealed class CapturingBeforeLoginHooks(Action<string> capture) : AuthenticationHooksBase
    {
        public override Task OnBeforeLoginAsync(BeforeLoginContext context, CancellationToken cancellationToken = default)
        {
            capture(context.UserName);
            return Task.CompletedTask;
        }
    }
}

internal sealed class FakeOtpRecipientResolver : IOtpRecipientResolver
{
    public OtpRecipient? Recipient { get; set; }

    public Task<OtpRecipient?> ResolveAsync(string normalizedPhoneNumber, CancellationToken cancellationToken = default) =>
        Task.FromResult(Recipient);
}

internal sealed class FakeOtpChallengeService : IOtpChallengeService
{
    public TenantId? TenantSeenOnIssue { get; private set; }

    public OtpIssueOutcome IssueOutcome { get; set; } = OtpIssueOutcome.Issued;
    public OtpVerifyResult VerifyResult { get; set; } = new(OtpVerifyOutcome.Invalid, null, null);
    public string? LastIssuePhone { get; private set; }
    public Guid? LastIssueUserId { get; private set; }

    public Task<OtpIssueResult> IssueAsync(string normalizedPhoneNumber, Guid? userId, CancellationToken cancellationToken = default)
    {
        TenantSeenOnIssue = TenantContextAccessor.CurrentTenantId;
        LastIssuePhone = normalizedPhoneNumber;
        LastIssueUserId = userId;
        return Task.FromResult(IssueOutcome == OtpIssueOutcome.Issued
            ? new OtpIssueResult(OtpIssueOutcome.Issued, Guid.NewGuid(), DateTimeOffset.Parse("2026-10-17T10:05:00Z"), "123456")
            : new OtpIssueResult(OtpIssueOutcome.Throttled, Guid.Empty, default, null));
    }

    public Task<OtpVerifyResult> VerifyAsync(Guid challengeId, string code, CancellationToken cancellationToken = default) =>
        Task.FromResult(VerifyResult);
}

internal sealed class FakeOtpCodeSender : IOtpCodeSender
{
    public List<OtpDelivery> Sent { get; } = [];
    public bool Throw { get; set; }
    public bool CancelOnSend { get; set; }

    public Task SendAsync(OtpDelivery delivery, CancellationToken cancellationToken = default)
    {
        if (CancelOnSend) throw new OperationCanceledException();
        if (Throw) throw new InvalidOperationException("gateway down, code was 123456");
        Sent.Add(delivery);
        return Task.CompletedTask;
    }
}

internal sealed class ListLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public List<(string Message, Exception? Exception)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((formatter(state, exception), exception));
}
