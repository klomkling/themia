using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Services;
using Themia.Modules.Identity.Tests.Fakes;
using Xunit;

namespace Themia.Modules.Identity.Tests.Services;

/// <summary>
/// A wrong guess is DECIDED once the password has been judged: a client that aborts the request at that moment must not
/// turn it into a free, uncharged guess, and the lock it triggers must still be recorded. A right guess decides nothing
/// until it is accepted, so its state change stays on the caller's token. The abort is signalled from inside the hasher
/// (where a real one lands), the store honours its token for the failure-state update like a real UPDATE, and the
/// assertions read the stored state.
/// </summary>
public class AbortedGuessTests
{
    private readonly List<User> store = [];
    private readonly FakeRepository<User> repo;
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-11-03T04:00:00Z"));
    private readonly IdentityModuleOptions options = new() { MaxFailedAccessAttempts = 3 };
    private readonly CancellationTokenSource abort = new();
    private readonly AbortingHasher hasher;
    private readonly LockProbeObserver observer = new();
    private readonly UserService sut;

    public AbortedGuessTests()
    {
        hasher = new AbortingHasher(abort);
        repo = new FakeRepository<User>(store, u => u.Id)
        {
            AmbientTenant = new TenantId("acme"),
            HonourCancellationFor = spec => spec.GetType().Name.Contains("UserFailureStateGate", StringComparison.Ordinal),
        };
        sut = new UserService(repo, new FakeUnitOfWork(), hasher, clock, options, new DataFilterScope(),
            new FormattingOnlyPhoneNumberNormalizer(), new RecordingUserLifecycleHooks(), [observer], NullLogger<UserService>.Instance);
    }

    private User AddUser(int failedCount = 0)
    {
        var user = new User
        {
            UserName = "victim", NormalizedUserName = "VICTIM", TenantId = new TenantId("acme"),
            PasswordHash = hasher.Hash("right"), AccessFailedCount = failedCount,
        };
        user.SetId(Guid.NewGuid());
        store.Add(user);
        return user;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task AWrongGuess_IsChargedAndAnsweredFailed_WhenTheClientAbortsWhileItIsBeingJudged(int failedBefore)
    {
        var user = AddUser(failedBefore);

        var result = await sut.VerifyPasswordAsync("victim", "wrong", abort.Token);

        Assert.Equal(PasswordVerificationResult.Failed, result);
        Assert.Equal(failedBefore + 1, user.AccessFailedCount);
        Assert.Null(user.LockoutEnd);
    }

    [Fact]
    public async Task TheLockingGuess_LocksTheAccountAndRaisesTheLock_WhenTheClientAbortsWhileItIsBeingJudged()
    {
        var user = AddUser(failedCount: 2);

        var result = await sut.VerifyPasswordAsync("victim", "wrong", abort.Token);

        Assert.Equal(PasswordVerificationResult.Failed, result);
        Assert.Equal(clock.GetUtcNow().Add(options.LockoutDuration), user.LockoutEnd);
        var seen = Assert.Single(observer.Locks);
        Assert.Equal(user.Id, seen.UserId);
        Assert.False(seen.TokenCancelled);
    }

    [Fact]
    public async Task AWrongGuess_IsChargedAfterReReadingTheState_WhenItLosesTheRaceAndTheClientAborts()
    {
        var user = AddUser();
        var lostOnce = false;
        repo.HonourCancellationForReads = _ => true;   // a read left on the caller's token would throw after the abort
        repo.BeforeUpdateWhere = _ =>
        {
            if (lostOnce) return;
            lostOnce = true;
            user.AccessFailedCount++;   // a parallel guess is charged just before this guess's gate UPDATE
        };

        var result = await sut.VerifyPasswordAsync("victim", "wrong", abort.Token);

        Assert.Equal(PasswordVerificationResult.Failed, result);
        Assert.Equal(2, user.AccessFailedCount);   // the parallel guess and this one, both charged
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task ARightGuess_IsNotAccepted_AndLeavesTheCounterAlone_WhenTheClientAbortsWhileItIsBeingJudged(int failedBefore)
    {
        // Accepting is not decided by the judgement: it passes the gate on the caller's token, so an abort surfaces
        // and the sign-in does not happen. The next login redoes it.
        var user = AddUser(failedBefore);

        await Assert.ThrowsAsync<OperationCanceledException>(() => sut.VerifyPasswordAsync("victim", "right", abort.Token));

        Assert.Equal(failedBefore, user.AccessFailedCount);
    }

    private sealed class AbortingHasher(CancellationTokenSource abort) : IPasswordHasher
    {
        public string Hash(string password) => "hash:" + password;

        public bool Verify(string encodedHash, string password)
        {
            abort.Cancel();   // the client disconnects while the (expensive) hash is being checked
            return encodedHash.EndsWith(":" + password, StringComparison.Ordinal);
        }

        public bool NeedsRehash(string encodedHash) => false;
    }

    private sealed class LockProbeObserver : IIdentityEventObserver
    {
        public sealed record Lock(Guid UserId, bool TokenCancelled);

        public List<Lock> Locks { get; } = [];

        public Task OnLockedOutAsync(Guid userId, DateTimeOffset lockoutEnd, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();   // an observer whose own write honours its token
            Locks.Add(new Lock(userId, cancellationToken.IsCancellationRequested));
            return Task.CompletedTask;
        }
    }
}
