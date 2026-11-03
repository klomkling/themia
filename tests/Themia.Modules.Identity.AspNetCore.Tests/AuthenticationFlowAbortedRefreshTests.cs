using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.AspNetCore.Authentication;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.Tests;

/// <summary>
/// coord #0193: a client that aborts the request once the refresh outcome is decided must not lose the audit
/// event. Every fake here honours its token the way the real store, principal factory and hooks do, and the
/// abort is signalled from inside <c>ValidateAndRotateAsync</c> because that is when a real abort lands.
/// </summary>
public sealed class AuthenticationFlowAbortedRefreshTests
{
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-11-03T03:00:00Z"));
    private readonly CancellationTokenSource abort = new();
    private readonly FakeRefreshTokenService refresh = new() { HonourCancellationOnResolveOwner = true };
    private readonly FakeClaimsPrincipalFactory factory = new() { HonourCancellation = true };
    private readonly RecordingHooks hooks = new() { HonourCancellationOnRefreshSucceeded = true };

    public AuthenticationFlowAbortedRefreshTests() => refresh.OnRotated = abort.Cancel;

    private AuthenticationFlow Build(params IIdentityEventObserver[] observers) =>
        new(new FakeUserService(), factory, new FakeAccessTokenService(), refresh, new FakePasswordHasher(), hooks,
            observers, clock, NullLogger<AuthenticationFlow>.Instance);

    private static RefreshIssue Successor() => new("new-refresh", DateTimeOffset.UtcNow.AddDays(14), Guid.NewGuid());

    [Fact]
    public async Task ReuseDetected_IsAuditedWithItsOwner_WhenTheClientAbortsAfterTheRotation()
    {
        var owner = Guid.NewGuid();
        refresh.OwnerId = owner;
        refresh.RotateResult = RefreshValidationResult.ReuseDetected();
        var observer = new TokenProbeObserver();

        await Build(observer).RefreshAsync("token", abort.Token);

        var seen = Assert.Single(observer.Events);
        Assert.Equal(nameof(IIdentityEventObserver.OnRefreshFailedAsync), seen.Name);
        Assert.Equal(owner, seen.UserId);
        Assert.False(seen.TokenCancelled);
    }

    [Fact]
    public async Task Replayed_IsAuditedAndStillSucceeds_WhenTheClientAbortsAfterTheRotation()
    {
        refresh.RotateResult = RefreshValidationResult.ReplayedWithinGrace(new User { UserName = "u" }, Successor());
        var observer = new TokenProbeObserver();

        var result = await Build(observer).RefreshAsync("token", abort.Token);

        Assert.True(result.Succeeded);
        Assert.Equal(
            [nameof(IIdentityEventObserver.OnRefreshSucceededAsync), nameof(IIdentityEventObserver.OnRefreshReplayedAsync)],
            observer.Events.Select(e => e.Name));
        Assert.All(observer.Events, e => Assert.False(e.TokenCancelled));
    }

    [Fact]
    public async Task LockedOutInvalid_IsAudited_WhenTheClientAbortsAfterTheRotation()
    {
        refresh.RotateResult = RefreshValidationResult.Success(new User { UserName = "u", IsActive = false }, Successor());
        var observer = new TokenProbeObserver();

        await Build(observer).RefreshAsync("token", abort.Token);

        var seen = Assert.Single(observer.Events);
        Assert.Equal(nameof(IIdentityEventObserver.OnRefreshFailedAsync), seen.Name);
        Assert.False(seen.TokenCancelled);
    }

    [Fact]
    public async Task CommittedDeny_IsAudited_WhenTheClientAbortsAfterTheRotation()
    {
        hooks.DenyRefreshSucceeded = true;
        refresh.RotateResult = RefreshValidationResult.Success(new User { UserName = "u" }, Successor());
        var observer = new TokenProbeObserver();

        await Build(observer).RefreshAsync("token", abort.Token);

        var seen = Assert.Single(observer.Events);
        Assert.Equal(nameof(IIdentityEventObserver.OnRefreshDeniedAsync), seen.Name);
        Assert.False(seen.TokenCancelled);
    }

    [Fact]
    public async Task PreRotationDeny_IsAudited_WhenTheClientHasAlreadyAborted()
    {
        hooks.DenyBeforeRefresh = true;
        await abort.CancelAsync();
        var observer = new TokenProbeObserver();

        await Build(observer).RefreshAsync("token", abort.Token);

        var seen = Assert.Single(observer.Events);
        Assert.Equal(nameof(IIdentityEventObserver.OnRefreshDeniedAsync), seen.Name);
        Assert.False(seen.TokenCancelled);
    }

    [Fact]
    public async Task ASlowObserver_IsNeverCutOff_WhileTheClientIsStillConnected()
    {
        refresh.OnRotated = null;
        refresh.RotateResult = RefreshValidationResult.Success(new User { UserName = "u" }, Successor());
        var observer = new TokenProbeObserver();
        using var live = new CancellationTokenSource();

        var refreshing = Build(observer).RefreshAsync("token", live.Token);
        clock.Advance(TimeSpan.FromHours(1));
        await refreshing;

        Assert.All(observer.Events, e => Assert.False(e.TokenCancelled));
    }

    [Fact]
    public async Task AHungObserver_IsSignalledTenSecondsAfterTheAbort_AndTheNextObserverStillGetsALiveToken()
    {
        refresh.RotateResult = RefreshValidationResult.ReuseDetected();
        var hung = new TokenProbeObserver { Hang = true };
        var next = new TokenProbeObserver();

        var refreshing = Build(hung, next).RefreshAsync("token", abort.Token);
        clock.Advance(TimeSpan.FromSeconds(10));
        await refreshing;

        Assert.True(Assert.Single(hung.Events).TokenCancelledOnExit);
        Assert.False(Assert.Single(next.Events).TokenCancelled);
    }

    // AuthenticationFlow raises every observer event through one path, so login and logout events get the same
    // post-abort grace. Pinned so the documented contract cannot drift from the code.
    [Fact]
    public async Task LoginFailed_ReceivesAToken_ThatIsNotCancelledAtTheAbort()
    {
        await abort.CancelAsync();
        var observer = new TokenProbeObserver();
        var flow = new AuthenticationFlow(
            new FakeUserService { VerifyResult = PasswordVerificationResult.Failed, UserToReturn = new User { UserName = "alice" } },
            factory, new FakeAccessTokenService(), refresh, new FakePasswordHasher(), hooks, [observer], clock,
            NullLogger<AuthenticationFlow>.Instance);

        await flow.LoginAsync("alice", "bad", abort.Token);

        var seen = Assert.Single(observer.Events);
        Assert.Equal(nameof(IIdentityEventObserver.OnLoginFailedAsync), seen.Name);
        Assert.False(seen.TokenCancelled);
    }

    [Fact]
    public async Task Logout_ReceivesAToken_ThatIsNotCancelledAtTheAbort()
    {
        await abort.CancelAsync();
        var observer = new TokenProbeObserver();

        await Build(observer).LogoutAsync("token", allSessions: false, abort.Token);

        var seen = Assert.Single(observer.Events);
        Assert.Equal(nameof(IIdentityEventObserver.OnLogoutAsync), seen.Name);
        Assert.False(seen.TokenCancelled);
    }

    [Fact]
    public async Task Logout_RevokesAndIsAudited_WhenTheClientAbortsBeforeTheRevoke()
    {
        await abort.CancelAsync();
        var owner = Guid.NewGuid();
        refresh.OwnerId = owner;
        refresh.HonourCancellationOnRevoke = true;
        var observer = new TokenProbeObserver();

        var outcome = await Build(observer).LogoutAsync("token", allSessions: true, abort.Token);

        Assert.Equal(LogoutOutcome.Revoked, outcome);
        Assert.Equal(1, refresh.RevokeCalls);
        var seen = Assert.Single(observer.Events);
        Assert.Equal(nameof(IIdentityEventObserver.OnLogoutAsync), seen.Name);
        Assert.Equal(owner, seen.UserId);
        Assert.False(seen.TokenCancelled);
    }

    [Fact]
    public async Task AnOwnerLookupThatOutlastsTheGrace_StillRaisesTheEventWithoutAnOwner()
    {
        refresh.RotateResult = RefreshValidationResult.ReuseDetected();
        refresh.OwnerId = Guid.NewGuid();
        refresh.HangResolveOwner = true;
        var observer = new TokenProbeObserver();

        var refreshing = Build(observer).RefreshAsync("token", abort.Token);
        clock.Advance(TimeSpan.FromSeconds(10));
        await refreshing;

        var seen = Assert.Single(observer.Events);
        Assert.Null(seen.UserId);
        Assert.False(seen.TokenCancelled);
    }
}

/// <summary>Records, per event, the user id and whether the token handed to it was already cancelled; can hang
/// on its token to model an observer stuck on a slow store.</summary>
internal sealed class TokenProbeObserver : IIdentityEventObserver
{
    public sealed record Seen(string Name, Guid? UserId, bool TokenCancelled, bool TokenCancelledOnExit);

    private readonly List<Seen> events = [];

    public IReadOnlyList<Seen> Events => events;

    /// <summary>When set, each event waits on its own token before returning.</summary>
    public bool Hang { get; set; }

    private async Task RecordAsync(string name, Guid? userId, CancellationToken cancellationToken)
    {
        var cancelledAtCall = cancellationToken.IsCancellationRequested;
        if (Hang)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The grace signalled the token: exactly what the hung observer is waiting for.
            }
        }

        events.Add(new Seen(name, userId, cancelledAtCall, cancellationToken.IsCancellationRequested));
    }

    public Task OnLoginFailedAsync(string userName, LoginFailureReason reason, CancellationToken cancellationToken = default) =>
        RecordAsync(nameof(OnLoginFailedAsync), null, cancellationToken);

    public Task OnLogoutAsync(Guid? userId, bool allSessions, CancellationToken cancellationToken = default) =>
        RecordAsync(nameof(OnLogoutAsync), userId, cancellationToken);

    public Task OnRefreshFailedAsync(Guid? userId, RefreshOutcome outcome, CancellationToken cancellationToken = default) =>
        RecordAsync(nameof(OnRefreshFailedAsync), userId, cancellationToken);

    public Task OnRefreshSucceededAsync(Guid userId, CancellationToken cancellationToken = default) =>
        RecordAsync(nameof(OnRefreshSucceededAsync), userId, cancellationToken);

    public Task OnRefreshReplayedAsync(Guid userId, CancellationToken cancellationToken = default) =>
        RecordAsync(nameof(OnRefreshReplayedAsync), userId, cancellationToken);

    public Task OnRefreshDeniedAsync(string? userName, string? reason, bool rotationCommitted, CancellationToken cancellationToken = default) =>
        RecordAsync(nameof(OnRefreshDeniedAsync), null, cancellationToken);
}
