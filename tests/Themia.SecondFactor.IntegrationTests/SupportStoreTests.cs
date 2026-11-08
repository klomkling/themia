using Npgsql;

using Themia.SecondFactor;

using Xunit;

namespace Themia.SecondFactor.IntegrationTests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupportStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private const int Parallelism = 24;

    private static string NewUser() => Guid.NewGuid().ToString("D");

    [Fact]
    public async Task RecoveryCode_ShouldBeConsumableExactlyOnce_WhenSubmittedInParallel()
    {
        var store = fixture.Get<IMfaRecoveryCodeStore>();
        var user = NewUser();
        await store.ReplaceAsync("t1", user, ["H1", "H2", "H3"], RecoveryCodeHasher.Scheme, Now);

        var results = await Task.WhenAll(Enumerable.Range(0, Parallelism).Select(_ => store.TryConsumeAsync("t1", user, "H2", Now)));

        Assert.Equal(1, results.Count(won => won));
        Assert.Equal(2, await store.CountUnusedAsync("t1", user));
    }

    [Fact]
    public async Task RecoveryCode_ShouldNotVerifyForAnotherUserOrTenant()
    {
        var store = fixture.Get<IMfaRecoveryCodeStore>();
        var user = NewUser();
        await store.ReplaceAsync(null, user, ["HX"], RecoveryCodeHasher.Scheme, Now);

        Assert.False(await store.TryConsumeAsync(null, NewUser(), "HX", Now));
        Assert.False(await store.TryConsumeAsync("t1", user, "HX", Now));
        Assert.True(await store.TryConsumeAsync(null, user, "HX", Now));
    }

    [Fact]
    public async Task RecoveryCode_ShouldReplaceOnlyUnusedCodes()
    {
        var store = fixture.Get<IMfaRecoveryCodeStore>();
        var user = NewUser();
        await store.ReplaceAsync(null, user, ["A", "B"], RecoveryCodeHasher.Scheme, Now);
        await store.TryConsumeAsync(null, user, "A", Now);

        await store.ReplaceAsync(null, user, ["C", "D", "E"], RecoveryCodeHasher.Scheme, Now);

        Assert.Equal(3, await store.CountUnusedAsync(null, user));
        Assert.False(await store.TryConsumeAsync(null, user, "B", Now));
    }

    [Fact]
    public async Task Failures_ShouldCountAcrossParallelCallsAndLockAtTheLimit()
    {
        var store = fixture.Get<IMfaUserStateStore>();
        var user = NewUser();

        var states = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => store.RecordFailureAsync(null, user, 5, TimeSpan.FromMinutes(15), Now)));

        var final = await store.GetLockStateAsync(null, user);
        Assert.Equal(10, states.Max(s => s.FailedAttempts));
        Assert.True(final.IsLocked(Now));
        Assert.False(final.IsLocked(Now.AddMinutes(16)));
    }

    [Fact]
    public async Task Failures_ShouldKeepOneRowPerPlatformUser()
    {
        var store = fixture.Get<IMfaUserStateStore>();
        var user = NewUser();

        await store.RecordFailureAsync(null, user, 5, TimeSpan.FromMinutes(1), Now);
        await store.RecordFailureAsync(null, user, 5, TimeSpan.FromMinutes(1), Now);

        Assert.Equal(2, (await store.GetLockStateAsync(null, user)).FailedAttempts);
    }

    [Fact]
    public async Task Failures_ShouldResetOnSuccess()
    {
        var store = fixture.Get<IMfaUserStateStore>();
        var user = NewUser();
        await store.RecordFailureAsync("t1", user, 2, TimeSpan.FromMinutes(5), Now);
        await store.RecordFailureAsync("t1", user, 2, TimeSpan.FromMinutes(5), Now);

        await store.ResetFailuresAsync("t1", user, Now);

        var state = await store.GetLockStateAsync("t1", user);
        Assert.Equal(0, state.FailedAttempts);
        Assert.False(state.IsLocked(Now));
    }

    [Fact]
    public async Task SpareNag_ShouldKeepTheFirstPromptTime()
    {
        var store = fixture.Get<IMfaUserStateStore>();
        var user = NewUser();

        Assert.Null(await store.GetSpareNagStartedAtAsync(null, user));
        await store.StartSpareNagAsync(null, user, Now);
        await store.StartSpareNagAsync(null, user, Now.AddDays(3));

        Assert.Equal(Now, await store.GetSpareNagStartedAtAsync(null, user));
    }

    [Fact]
    public async Task Ceremony_ShouldBeConsumableOnce_BoundToUserAndPurpose()
    {
        var store = fixture.Get<IMfaCeremonyStore>();
        var user = NewUser();
        var id = Guid.NewGuid().ToString("N");
        await store.StoreAsync(id, "{}", null, user, MfaCeremonyPurpose.Register, TimeSpan.FromMinutes(5));

        Assert.Null(await store.TryConsumeAsync(id, null, user, MfaCeremonyPurpose.Authenticate));
        Assert.Null(await store.TryConsumeAsync(id, null, NewUser(), MfaCeremonyPurpose.Register));
        Assert.NotNull(await store.TryConsumeAsync(id, null, user, MfaCeremonyPurpose.Register));
        Assert.Null(await store.TryConsumeAsync(id, null, user, MfaCeremonyPurpose.Register));
    }

    [Fact]
    public async Task Ceremony_ShouldLetExactlyOneConsumerWin_WhenRacing()
    {
        var store = fixture.Get<IMfaCeremonyStore>();
        var user = NewUser();
        var id = Guid.NewGuid().ToString("N");
        await store.StoreAsync(id, "{}", null, user, MfaCeremonyPurpose.Authenticate, TimeSpan.FromMinutes(5));

        var results = await Task.WhenAll(Enumerable.Range(0, Parallelism)
            .Select(_ => store.TryConsumeAsync(id, null, user, MfaCeremonyPurpose.Authenticate)));

        Assert.Equal(1, results.Count(r => r is not null));
    }

    [Fact]
    public async Task Ceremony_ShouldRefuseAnExpiredOne()
    {
        var store = fixture.Get<IMfaCeremonyStore>();
        var id = Guid.NewGuid().ToString("N");
        await store.StoreAsync(id, "{}", null, null, MfaCeremonyPurpose.Authenticate, TimeSpan.FromSeconds(-1));

        Assert.Null(await store.TryConsumeAsync(id, null, null, MfaCeremonyPurpose.Authenticate));
    }

    [Fact]
    public async Task Audit_ShouldAppend_AndRefuseUpdateDeleteAndTruncate()
    {
        var audit = fixture.Get<IMfaAuditLog>();
        await audit.AppendAsync(new MfaAuditEntry(null, NewUser(), "admin-1", "admin.reset", MfaMethodKind.Totp, "lost phone"), Now);

        await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsync("UPDATE second_factor_audit SET actor = 'x'"));
        await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsync("DELETE FROM second_factor_audit"));
        await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsync("TRUNCATE second_factor_audit"));
    }

    [Fact]
    public async Task Ceremony_ShouldBeBoundToTheTenantItWasOpenedFor()
    {
        var store = fixture.Get<IMfaCeremonyStore>();
        var user = NewUser();
        var id = Guid.NewGuid().ToString("N");
        await store.StoreAsync(id, "{}", "tenant-a", user, MfaCeremonyPurpose.Register, TimeSpan.FromMinutes(5));

        Assert.Null(await store.TryConsumeAsync(id, "tenant-b", user, MfaCeremonyPurpose.Register));
        Assert.Null(await store.TryConsumeAsync(id, null, user, MfaCeremonyPurpose.Register));
        Assert.NotNull(await store.TryConsumeAsync(id, "tenant-a", user, MfaCeremonyPurpose.Register));
    }

    [Theory]
    [InlineData(MfaCeremonyPurpose.Register)]
    [InlineData(MfaCeremonyPurpose.EnrolTotp)]
    public async Task Ceremony_ShouldRefuseToStoreAUserlessRegisterOrEnrol(MfaCeremonyPurpose purpose)
    {
        var store = fixture.Get<IMfaCeremonyStore>();

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.StoreAsync(Guid.NewGuid().ToString("N"), "{}", null, null, purpose, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task Ceremony_ShouldPurgeOnlyExpiredRows()
    {
        var store = fixture.Get<IMfaCeremonyStore>();
        var live = Guid.NewGuid().ToString("N");
        var dead = Guid.NewGuid().ToString("N");
        await store.StoreAsync(live, "{}", null, null, MfaCeremonyPurpose.Authenticate, TimeSpan.FromMinutes(5));
        await store.StoreAsync(dead, "{}", null, null, MfaCeremonyPurpose.Authenticate, TimeSpan.FromSeconds(-1));

        var purged = await store.PurgeExpiredAsync();

        Assert.True(purged >= 1);
        Assert.NotNull(await store.TryConsumeAsync(live, null, null, MfaCeremonyPurpose.Authenticate));
    }

    [Fact]
    public async Task RecoveryCode_ShouldLeaveOneBatch_WhenRegeneratedConcurrently()
    {
        var store = fixture.Get<IMfaRecoveryCodeStore>();
        var user = NewUser();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            store.ReplaceAsync("t1", user, [$"B{i}-1", $"B{i}-2", $"B{i}-3"], RecoveryCodeHasher.Scheme, Now)));

        Assert.Equal(3, await store.CountUnusedAsync("t1", user));
    }
}
