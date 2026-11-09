using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Themia.SecondFactor.DependencyInjection;
using Xunit;

namespace Themia.SecondFactor.Tests;

public sealed class SecondFactorAdminServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly MfaSubject Actor = new("actor", "tenant-a", new HashSet<string>());
    private static readonly MfaProof Fresh = new(MfaStrength.Strong, Now.AddMinutes(-1), RecoveryCodeOnly: false);

    private readonly FakeResetStore store = new();
    private readonly FakeActOn actOn = new();
    private readonly List<ISecondFactorResetObserver> observers = [];

    private SecondFactorAdminService Service() => new(
        store,
        actOn,
        observers,
        Options.Create(new SecondFactorOptions()),
        new FakeTimeProvider(Now),
        NullLogger<SecondFactorAdminService>.Instance);

    [Fact]
    public async Task ResetAsync_ShouldResetAndAudit_WhenEveryCheckPasses()
    {
        var outcome = await Service().ResetAsync(Actor, Fresh, "target", "lost phone");

        Assert.Equal(MfaAdminResetOutcome.Succeeded, outcome);
        var call = Assert.Single(store.Calls);
        Assert.Equal(("tenant-a", "target", "actor", "admin.reset", "lost phone"), (call.Tenant, call.User, call.Actor, call.Action, call.Reason));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("weak")]
    [InlineData("recovery")]
    [InlineData("stale")]
    public async Task ResetAsync_ShouldRequireAFreshStrongProof(string? kind)
    {
        MfaProof? proof = kind switch
        {
            "weak" => Fresh with { Strength = MfaStrength.Weak },
            "recovery" => Fresh with { RecoveryCodeOnly = true },
            "stale" => Fresh with { AuthenticatedAt = Now.AddHours(-1) },
            _ => null,
        };

        Assert.Equal(MfaAdminResetOutcome.StepUpRequired, await Service().ResetAsync(Actor, proof, "target", "lost phone"));
        Assert.Empty(store.Calls);
        Assert.Equal(0, actOn.Asked);
    }

    [Fact]
    public async Task ResetAsync_ShouldRefuseResettingYourself_BeforeAskingThePolicy()
    {
        Assert.Equal(MfaAdminResetOutcome.NotAllowed, await Service().ResetAsync(Actor, Fresh, "actor", "lost phone"));
        Assert.Empty(store.Calls);
        Assert.Equal(0, actOn.Asked);
    }

    [Theory]
    [InlineData("", "lost phone")]
    [InlineData("  ", "lost phone")]
    [InlineData("target", "")]
    [InlineData("target", "   ")]
    public async Task ResetAsync_ShouldRejectABlankTargetOrReason(string target, string reason)
    {
        Assert.Equal(MfaAdminResetOutcome.InvalidRequest, await Service().ResetAsync(Actor, Fresh, target, reason));
        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task ResetAsync_ShouldRejectAReasonLongerThanTheCap()
    {
        var reason = new string('x', SecondFactorAdminService.MaxReasonLength + 1);

        Assert.Equal(MfaAdminResetOutcome.InvalidRequest, await Service().ResetAsync(Actor, Fresh, "target", reason));
        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task ResetAsync_ShouldRefuse_WhenThePolicyDenies()
    {
        actOn.Result = false;

        Assert.Equal(MfaAdminResetOutcome.NotAllowed, await Service().ResetAsync(Actor, Fresh, "target", "lost phone"));
        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task ResetAsync_ShouldRefuse_WhenThePolicyThrows()
    {
        actOn.Throw = new InvalidOperationException("db down");

        Assert.Equal(MfaAdminResetOutcome.NotAllowed, await Service().ResetAsync(Actor, Fresh, "target", "lost phone"));
        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task ResetAsync_ShouldPropagateCancellation_FromThePolicy()
    {
        actOn.Throw = new OperationCanceledException();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().ResetAsync(Actor, Fresh, "target", "lost phone"));
        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task ResetAsync_ShouldReportSessionsNotRevoked_WithoutUndoingTheReset_WhenAnObserverThrows()
    {
        observers.Add(new FakeObserver(new InvalidOperationException("revoke failed")));
        var later = new FakeObserver(null);
        observers.Add(later);

        var outcome = await Service().ResetAsync(Actor, Fresh, "target", "lost phone");

        Assert.Equal(MfaAdminResetOutcome.SessionsNotRevoked, outcome);
        Assert.Single(store.Calls);
        Assert.Equal(1, later.Calls);
    }

    [Fact]
    public async Task ResetAsync_ShouldRunObserversAfterTheStoreReset()
    {
        var observer = new FakeObserver(null) { SawResetAlreadyApplied = () => store.Calls.Count == 1 };
        observers.Add(observer);

        Assert.Equal(MfaAdminResetOutcome.Succeeded, await Service().ResetAsync(Actor, Fresh, "target", "lost phone"));
        Assert.True(observer.SawReset);
    }

    [Fact]
    public async Task ResetAsync_ShouldNotRevoke_WhenTheStoreThrows()
    {
        store.Throw = new InvalidOperationException("audit failed");
        var observer = new FakeObserver(null);
        observers.Add(observer);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ResetAsync(Actor, Fresh, "target", "lost phone"));
        Assert.Equal(0, observer.Calls);
    }

    [Fact]
    public void Validate_ShouldFail_WhenNoActOnPolicyIsRegistered()
    {
        var result = new AdminPolicyValidator(new ServiceCollection()).Validate(null, new SecondFactorOptions());

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_ShouldSucceed_WhenAnActOnPolicyIsRegistered()
    {
        var services = new ServiceCollection().AddSingleton<IMfaActOnPolicy>(actOn);

        Assert.True(new AdminPolicyValidator(services).Validate(null, new SecondFactorOptions()).Succeeded);
    }

    private sealed class FakeResetStore : IMfaResetStore
    {
        public List<(string? Tenant, string User, string Actor, string Action, string Reason)> Calls { get; } = [];

        public Exception? Throw { get; set; }

        public Task<int> ResetAsync(string? tenantId, string userId, string actor, string action, string reason, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            Calls.Add((tenantId, userId, actor, action, reason));
            return Task.FromResult(1);
        }
    }

    private sealed class FakeActOn : IMfaActOnPolicy
    {
        public bool Result { get; set; } = true;

        public Exception? Throw { get; set; }

        public int Asked { get; private set; }

        public Task<bool> CanActOnAsync(MfaSubject actor, string targetUserId, CancellationToken cancellationToken = default)
        {
            Asked++;
            return Throw is null ? Task.FromResult(Result) : Task.FromException<bool>(Throw);
        }
    }

    private sealed class FakeObserver(Exception? fail) : ISecondFactorResetObserver
    {
        public int Calls { get; private set; }

        public bool SawReset { get; private set; }

        public Func<bool>? SawResetAlreadyApplied { get; init; }

        public Task OnResetAsync(string? tenantId, string targetUserId, CancellationToken cancellationToken = default)
        {
            Calls++;
            SawReset = SawResetAlreadyApplied?.Invoke() ?? false;
            return fail is null ? Task.CompletedTask : Task.FromException(fail);
        }
    }
}
