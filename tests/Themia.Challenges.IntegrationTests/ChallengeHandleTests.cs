using System.Threading;

using Xunit;

namespace Themia.Challenges.IntegrationTests;

/// <summary>
/// <see cref="IChallengeHandles"/> (coord #0232) on the real engines: a stranger who knows only the address cannot
/// spend what the owner's verify depends on, and one correct code still wins exactly once.
/// </summary>
public abstract class ChallengeHandleTests
{
    private const string Wrong = "000000";
    private readonly ChallengeEngineFixture fixture;

    /// <summary>Creates the suite over one engine's <paramref name="fixture"/>.</summary>
    protected ChallengeHandleTests(ChallengeEngineFixture fixture) => this.fixture = fixture;

    /// <summary>Garbage codes against the address and a stranger's own handle both leave the owner's handle verifiable.</summary>
    [Fact]
    public async Task VerifyByHandle_Verifies_WhenAStrangerBurnedTheAddressAndTheirOwnHandle()
    {
        var scope = NewScope();
        var (ownerHandle, ownerCode) = await IssueAsync(scope);
        var (strangerHandle, _) = await IssueAsync(scope);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.VerifyAsync(scope, Wrong));
        for (var i = 0; i < 30; i++)
        {
            await fixture.Handles.VerifyByHandleAsync(strangerHandle, ChallengeEngineFixture.HandlePurpose, Wrong, scope.TenantId);
        }

        Assert.Equal(
            ChallengeVerifyOutcome.Verified,
            (await fixture.Handles.VerifyByHandleAsync(ownerHandle, ChallengeEngineFixture.HandlePurpose, ownerCode, scope.TenantId)).Outcome);
    }

    /// <summary>An unknown handle or a wrong purpose or tenant is NotFound.</summary>
    [Fact]
    public async Task VerifyByHandle_ReturnsNotFound_ForUnknownHandleAndForeignPurposeOrTenant()
    {
        var scope = NewScope();
        var (handle, code) = await IssueAsync(scope);

        Assert.Equal(ChallengeVerifyOutcome.NotFound, (await fixture.Handles.VerifyByHandleAsync(Guid.NewGuid(), scope.Purpose, code, scope.TenantId)).Outcome);
        Assert.Equal(ChallengeVerifyOutcome.NotFound, (await fixture.Handles.VerifyByHandleAsync(handle, scope.Purpose, code, "other-tenant")).Outcome);
        Assert.Equal(ChallengeVerifyOutcome.NotFound, (await fixture.Handles.VerifyByHandleAsync(handle, ChallengeEngineFixture.GenericPurpose, code, scope.TenantId)).Outcome);
    }

    /// <summary>The attempt budget of the handle's own row runs out, and the correct code is then refused.</summary>
    [Fact]
    public async Task VerifyByHandle_ReportsExhausted_AfterMaxAttempts()
    {
        var scope = NewScope();
        var (handle, code) = await IssueAsync(scope);

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(ChallengeVerifyOutcome.Incorrect, (await fixture.Handles.VerifyByHandleAsync(handle, scope.Purpose, Wrong, scope.TenantId)).Outcome);
        }

        Assert.Equal(ChallengeVerifyOutcome.AttemptsExhausted, (await fixture.Handles.VerifyByHandleAsync(handle, scope.Purpose, Wrong, scope.TenantId)).Outcome);
        Assert.Equal(ChallengeVerifyOutcome.AttemptsExhausted, (await fixture.Handles.VerifyByHandleAsync(handle, scope.Purpose, code, scope.TenantId)).Outcome);
    }

    /// <summary>Concurrent correct submits produce exactly one Verified.</summary>
    [Fact]
    public async Task VerifyByHandle_VerifiesExactlyOnce_WhenSubmittedConcurrently()
    {
        var scope = NewScope();
        var (handle, code) = await IssueAsync(scope);

        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => Task.Run(() => fixture.Handles.VerifyByHandleAsync(handle, scope.Purpose, code, scope.TenantId))));

        Assert.Equal(1, results.Count(r => r.Outcome == ChallengeVerifyOutcome.Verified));
        Assert.DoesNotContain(results, r => r.Outcome == ChallengeVerifyOutcome.Incorrect);
    }

    private static ChallengeScope NewScope() =>
        new($"owner-{Guid.NewGuid():N}@example.com", ChallengeEngineFixture.HandlePurpose, "tenant-a");

    private async Task<(Guid Handle, string Code)> IssueAsync(ChallengeScope scope)
    {
        var issued = await fixture.Service.IssueAsync(scope);
        Assert.True(issued.Succeeded);
        return (issued.ChallengeId!.Value, issued.Secret!);
    }

    /// <summary>PostgreSQL execution of <see cref="ChallengeHandleTests"/>.</summary>
    [Collection(PostgresChallengesCollection.Name)]
    [Trait("Category", "Integration")]
    public sealed class PostgresChallengeHandleTests : ChallengeHandleTests
    {
        /// <summary>Creates the suite over the shared <see cref="PostgresChallengeFixture"/> container.</summary>
        public PostgresChallengeHandleTests(PostgresChallengeFixture fixture) : base(fixture)
        {
        }
    }

    /// <summary>MySQL execution of <see cref="ChallengeHandleTests"/>.</summary>
    [Collection(MySqlChallengesCollection.Name)]
    [Trait("Category", "Integration")]
    public sealed class MySqlChallengeHandleTests : ChallengeHandleTests
    {
        /// <summary>Creates the suite over the shared <see cref="MySqlChallengeFixture"/> container.</summary>
        public MySqlChallengeHandleTests(MySqlChallengeFixture fixture) : base(fixture)
        {
        }
    }

    /// <summary>SQL Server execution of <see cref="ChallengeHandleTests"/>.</summary>
    [Collection(SqlServerChallengesCollection.Name)]
    [Trait("Category", "Integration")]
    public sealed class SqlServerChallengeHandleTests : ChallengeHandleTests
    {
        /// <summary>Creates the suite over the shared <see cref="SqlServerChallengeFixture"/> container.</summary>
        public SqlServerChallengeHandleTests(SqlServerChallengeFixture fixture) : base(fixture)
        {
        }
    }
}
