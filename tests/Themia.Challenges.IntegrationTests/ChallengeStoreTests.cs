using Dapper;

using Xunit;

namespace Themia.Challenges.IntegrationTests;

/// <summary>
/// Proves three claims that cannot be faked against an in-memory/SQLite double (see
/// <c>ChallengeServiceTests</c> in <c>Themia.Challenges.Tests</c>, which already covers everything else
/// in the security-requirements list):
/// <list type="number">
/// <item><description>
/// <see cref="Migrations.ChallengeSchemaMigration"/> actually creates both tables and every index on
/// each engine's own catalog — not just that FluentMigrator ran without throwing. This is what catches
/// an <c>InSchema(...)</c> that silently vanished on MySQL, or a filtered-index expression that only
/// one engine's SQL dialect accepts.
/// </description></item>
/// <item><description>
/// Cross-tenant isolation holds against a real engine's null-safe comparison, not merely that the SQL
/// text compiles. All three dialects express "match this tenant, including a null one" differently
/// (<c>IS NOT DISTINCT FROM</c> / <c>&lt;=&gt;</c> / the SQL Server OR-guard) — this is the only place
/// that distinction is exercised against a live database per engine.
/// </description></item>
/// <item><description>
/// Hashing is real: a persisted <c>challenges</c> row, read back with no help from
/// <c>Themia.Challenges</c>'s own code, never contains the plaintext secret in any column.
/// </description></item>
/// </list>
/// The hashing assertion runs on PostgreSQL only (see <see cref="PostgresChallengeStoreTests"/>):
/// <c>SecretHasher</c> has no per-engine behavior, and the shape of a persisted row is identical across
/// all three dialects' <c>InsertSql</c>, so a second and third run would prove nothing the first didn't
/// already prove twelve times over.
/// </summary>
public abstract class ChallengeStoreTests
{
    private readonly ChallengeEngineFixture fixture;

    /// <summary>Creates the shared-behavior suite over one engine's <paramref name="fixture"/>.</summary>
    protected ChallengeStoreTests(ChallengeEngineFixture fixture) => this.fixture = fixture;

    // ---- Schema ----

    [Fact]
    public async Task SchemaMigration_CreatesBothTablesAndEveryIndex()
    {
        var tables = await fixture.GetTableNamesAsync();
        Assert.Contains("challenges", tables);
        Assert.Contains("challenge_rate_windows", tables);

        var indexes = await fixture.GetIndexNamesAsync();
        Assert.Contains("ix_challenges_scope", indexes);
        Assert.Contains("ix_challenges_token_hash", indexes);
        // The two retention indexes. Both purge statements filter on one column with no other predicate,
        // so without these the hourly purge full-scans the tables every issue and verify contends on.
        Assert.Contains("ix_challenges_expires_at", indexes);
        Assert.Contains("ix_challenge_rate_windows_window_start", indexes);
        // The four filtered/functional unique indexes that together give challenge_rate_windows
        // exact-one-row-per-bucket semantics (see ChallengeSchemaMigration.CreateRateWindowUniqueIndexes'
        // remarks) — this is exactly the kind of thing that silently fails to create on one engine while
        // the migration otherwise reports success.
        Assert.Contains("ux_challenge_rate_windows_tenant_purpose", indexes);
        Assert.Contains("ux_challenge_rate_windows_tenant_keyonly", indexes);
        Assert.Contains("ux_challenge_rate_windows_platform_purpose", indexes);
        Assert.Contains("ux_challenge_rate_windows_platform_keyonly", indexes);
    }

    // ---- Cross-tenant isolation ----

    [Fact]
    public async Task Verify_ShouldNotSucceed_ForACodeIssuedToADifferentTenant()
    {
        var key = UniqueKey();
        var scopeA = new ChallengeScope(key, ChallengeEngineFixture.GenericPurpose, UniqueTenant());
        var scopeB = scopeA with { TenantId = UniqueTenant() };

        var issue = await fixture.Service.IssueAsync(scopeA);
        Assert.Equal(ChallengeIssueOutcome.Issued, issue.Outcome);

        var crossTenantResult = await fixture.Service.VerifyAsync(scopeB, issue.Secret!);
        Assert.NotEqual(ChallengeVerifyOutcome.Verified, crossTenantResult.Outcome);

        // The same code must still work for the tenant it was actually issued to — proves this is real
        // tenant scoping, not a bug that happens to reject every verification regardless of tenant.
        var ownTenantResult = await fixture.Service.VerifyAsync(scopeA, issue.Secret!);
        Assert.Equal(ChallengeVerifyOutcome.Verified, ownTenantResult.Outcome);
    }

    [Fact]
    public async Task RateLimit_ExhaustingOneTenantsCeiling_ShouldNotAffectAnotherTenantWithTheSameKey()
    {
        var key = UniqueKey();
        var scopeA = new ChallengeScope(key, ChallengeEngineFixture.TightPurpose, UniqueTenant());
        var scopeB = scopeA with { TenantId = UniqueTenant() };
        var service = fixture.CreateServiceWithTightKeyCeiling();

        for (var i = 0; i < ChallengeEngineFixture.TightPerKeyLimit; i++)
        {
            var result = await service.IssueAsync(scopeA);
            Assert.Equal(ChallengeIssueOutcome.Issued, result.Outcome);
        }

        var exhausted = await service.IssueAsync(scopeA);
        Assert.Equal(ChallengeIssueOutcome.RateLimited, exhausted.Outcome);

        // Tenant B shares the exact same physical key but is a different tenant — its per-key ceiling
        // is a separate bucket (tenant_id is part of the composite key on both tables), so it must be
        // unaffected by tenant A having just exhausted theirs.
        var tenantBResult = await service.IssueAsync(scopeB);
        Assert.Equal(ChallengeIssueOutcome.Issued, tenantBResult.Outcome);
    }

    [Fact]
    public async Task Verify_ShouldNotSucceed_ForACodeIssuedToAKeyDifferingOnlyByCase()
    {
        // ChallengeScope.Key is documented as opaque and never parsed, so an adopter may legitimately use
        // a case-sensitive user id. MySQL 8 and SQL Server both default to a case-folding collation, and
        // every dialect compares `key` with plain `=` — without the collation pinned by
        // ChallengeSchemaMigration.PinComparedColumnCollation, a code issued for one key verifies against
        // a different account whose key differs only by case, and the two share one rate-limit bucket.
        var tenantId = UniqueTenant();
        var lower = $"case-{Guid.NewGuid():N}".ToLowerInvariant();
        var upper = lower.ToUpperInvariant();

        var lowerScope = new ChallengeScope(lower, ChallengeEngineFixture.GenericPurpose, tenantId);
        var upperScope = new ChallengeScope(upper, ChallengeEngineFixture.GenericPurpose, tenantId);

        var issued = await fixture.Service.IssueAsync(lowerScope);
        Assert.Equal(ChallengeIssueOutcome.Issued, issued.Outcome);

        var crossCase = await fixture.Service.VerifyAsync(upperScope, issued.Secret!);
        Assert.NotEqual(ChallengeVerifyOutcome.Verified, crossCase.Outcome);

        // The code still works for the key it was actually issued to — this must fail closed on the
        // wrong key, not break the right one.
        var ownKey = await fixture.Service.VerifyAsync(lowerScope, issued.Secret!);
        Assert.Equal(ChallengeVerifyOutcome.Verified, ownKey.Outcome);
    }

    /// <summary>A fresh, collision-free scope key — every test shares one running container's schema, so
    /// isolation between tests comes from never reusing a key/tenant, not from any test-scoped cleanup.</summary>
    protected static string UniqueKey() => $"key-{Guid.NewGuid():N}";

    /// <summary>A fresh, collision-free tenant id — see <see cref="UniqueKey"/>.</summary>
    protected static string UniqueTenant() => $"tenant-{Guid.NewGuid():N}";

    /// <summary>PostgreSQL execution of <see cref="ChallengeStoreTests"/>, plus the hashing assertion —
    /// see the type's remarks on why hashing runs on one engine only.</summary>
    [Collection(PostgresChallengesCollection.Name)]
    [Trait("Category", "Integration")]
    public sealed class PostgresChallengeStoreTests : ChallengeStoreTests
    {
        private readonly PostgresChallengeFixture postgresFixture;

        /// <summary>Creates the suite over the shared <see cref="PostgresChallengeFixture"/> container.</summary>
        public PostgresChallengeStoreTests(PostgresChallengeFixture fixture) : base(fixture) => postgresFixture = fixture;

        [Fact]
        public async Task IssuedChallenge_ShouldNeverPersistThePlaintextSecretInAnyColumn()
        {
            var key = UniqueKey();
            var scope = new ChallengeScope(key, ChallengeEngineFixture.GenericPurpose, UniqueTenant());

            var issue = await postgresFixture.Service.IssueAsync(scope);
            Assert.Equal(ChallengeIssueOutcome.Issued, issue.Outcome);
            var secret = issue.Secret!;

            await using var connection = postgresFixture.Dialect.CreateConnection();
            await connection.OpenAsync();
            var row = await connection.QueryFirstAsync(
                $"SELECT * FROM challenges WHERE {postgresFixture.KeyColumn} = @Key AND tenant_id = @TenantId",
                new { scope.Key, scope.TenantId });

            // Read every column back with no help from Themia.Challenges' own types (no ChallengeRow, no
            // SecretHasher) — a plain dynamic row, exactly what a support engineer or a DB client would see.
            IDictionary<string, object> columns = row;
            Assert.NotEmpty(columns);
            foreach (var (columnName, value) in columns)
            {
                if (value is string text)
                {
                    Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
                }
                Assert.NotEqual(secret, value?.ToString());
            }
        }
    }

    /// <summary>MySQL execution of <see cref="ChallengeStoreTests"/>.</summary>
    [Collection(MySqlChallengesCollection.Name)]
    [Trait("Category", "Integration")]
    public sealed class MySqlChallengeStoreTests : ChallengeStoreTests
    {
        /// <summary>Creates the suite over the shared <see cref="MySqlChallengeFixture"/> container.</summary>
        public MySqlChallengeStoreTests(MySqlChallengeFixture fixture) : base(fixture)
        {
        }
    }

    /// <summary>SQL Server execution of <see cref="ChallengeStoreTests"/>.</summary>
    [Collection(SqlServerChallengesCollection.Name)]
    [Trait("Category", "Integration")]
    public sealed class SqlServerChallengeStoreTests : ChallengeStoreTests
    {
        /// <summary>Creates the suite over the shared <see cref="SqlServerChallengeFixture"/> container.</summary>
        public SqlServerChallengeStoreTests(SqlServerChallengeFixture fixture) : base(fixture)
        {
        }
    }
}
