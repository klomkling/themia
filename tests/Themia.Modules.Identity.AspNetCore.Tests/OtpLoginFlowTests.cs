using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.AspNetCore.Authentication;
using Themia.Modules.Identity.Services;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.Tests;

public sealed class OtpLoginFlowTests
{
    private const string RawPhone = "081-234-5678";
    private const string NormalizedPhone = "0812345678";
    private const string Code = "123456";

    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-10-17T10:00:00Z"));
    private readonly FakeUserService users = new();
    private readonly FakeOtpChallengeService challenges = new();
    private readonly FakeOtpCodeSender sender = new();
    private readonly FakeRefreshTokenService refresh = new();
    private readonly RecordingHooks hooks = new();
    private readonly RecordingObserver observer = new();
    private readonly FakeAccessTokenService access;
    private readonly OtpLoginFlow flow;

    public OtpLoginFlowTests()
    {
        access = new FakeAccessTokenService(clock);
        hooks.Refresh = refresh;
        flow = new OtpLoginFlow(users, challenges, sender, new FormattingOnlyPhoneNumberNormalizer(),
            new FakeClaimsPrincipalFactory(), access, refresh, hooks, [observer], clock, NullLogger<OtpLoginFlow>.Instance);
    }

    private static User Eligible() => new()
    {
        UserName = "alice", IsActive = true, PhoneNumber = RawPhone, NormalizedPhoneNumber = NormalizedPhone, PhoneNumberConfirmed = true,
    };

    private void GivenAccount(User? user = null)
    {
        var u = user ?? Eligible();
        users.PhoneUserToReturn = u;
        users.UserToReturn = u;
        challenges.VerifyResult = new OtpVerifyResult(OtpVerifyOutcome.Verified, u.Id, NormalizedPhone);
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

    [Theory]
    [InlineData("unconfirmed")]
    [InlineData("inactive")]
    [InlineData("locked")]
    public async Task Start_ForAnIneligibleAccountIsADecoyAndSendsNothing(string kind)
    {
        var user = Eligible();
        switch (kind)
        {
            case "unconfirmed": user.PhoneNumberConfirmed = false; break;
            case "inactive": user.IsActive = false; break;
            default: user.LockoutEnd = clock.GetUtcNow().AddMinutes(5); break;
        }

        GivenAccount(user);
        var result = await flow.StartAsync(RawPhone);

        Assert.True(result.Issued);
        Assert.Null(challenges.LastIssueUserId);
        Assert.Empty(sender.Sent);
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
        var logging = new OtpLoginFlow(users, challenges, sender, new FormattingOnlyPhoneNumberNormalizer(),
            new FakeClaimsPrincipalFactory(), access, refresh, hooks, [], clock, log);

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
        var capturingFlow = new OtpLoginFlow(users, challenges, sender, new FormattingOnlyPhoneNumberNormalizer(),
            new FakeClaimsPrincipalFactory(), access, refresh, capturing, [], clock, NullLogger<OtpLoginFlow>.Instance);

        await capturingFlow.VerifyAsync(Guid.NewGuid(), Code);

        Assert.Equal(NormalizedPhone, seen);
    }

    [Fact]
    public async Task Start_RejectsABlankPhone()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => flow.StartAsync("  "));
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

internal sealed class FakeOtpChallengeService : IOtpChallengeService
{
    public OtpIssueOutcome IssueOutcome { get; set; } = OtpIssueOutcome.Issued;
    public OtpVerifyResult VerifyResult { get; set; } = new(OtpVerifyOutcome.Invalid, null, null);
    public string? LastIssuePhone { get; private set; }
    public Guid? LastIssueUserId { get; private set; }

    public Task<OtpIssueResult> IssueAsync(string normalizedPhoneNumber, Guid? userId, CancellationToken cancellationToken = default)
    {
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
