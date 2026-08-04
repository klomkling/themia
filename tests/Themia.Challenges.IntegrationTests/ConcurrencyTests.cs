using System.Transactions;

using Dapper;

using Microsoft.Extensions.Logging.Abstractions;

using Themia.Challenges.Internal;

using Xunit;

namespace Themia.Challenges.IntegrationTests;

/// <summary>
/// The two claims neither <c>ChallengeServiceTests</c> (SQLite, one writer at a time) nor reading the
/// SQL text can prove: that <see cref="IChallengeDialect.ConsumeSql"/> is genuinely atomic under two
/// real, simultaneous connections, and that <see cref="IChallengeDialect.IncrementWindowSql"/>'s
/// two-statement upsert never drops an increment when many connections race on the very first bucket.
/// Every dialect author deferred exactly this proof to this class — see each dialect's own remarks.
/// </summary>
public abstract class ConcurrencyTests
{
    /// <summary>
    /// Concurrency level for <see cref="ConcurrentIssues_ForTheSameKey_ShouldNotLoseACount"/>. Two
    /// racing writers rarely expose a lost update — both can plausibly land on the "seed the bucket"
    /// branch or serialize by luck. 64 was chosen because it comfortably exceeds the handful of
    /// connections a Testcontainers-backed engine can accept and service inside a single test's
    /// lifetime while still forcing real contention on the bucket's unique index: verified separately
    /// (see the task report) against a deliberately non-atomic
    /// select-count-then-update-in-two-round-trips stand-in for <c>IncrementWindowSql</c>, which lost
    /// updates and undercounted at this concurrency level on every run.
    /// </summary>
    private const int ConcurrencyLevel = 64;

    private readonly ChallengeEngineFixture fixture;

    /// <summary>Creates the concurrency suite over one engine's <paramref name="fixture"/>.</summary>
    protected ConcurrencyTests(ChallengeEngineFixture fixture) => this.fixture = fixture;

    /// <summary>
    /// The requirement most likely to be quietly broken by a later refactor to read-then-write, and the
    /// one that cannot be proven without two genuinely separate connections racing on the same row:
    /// <see cref="ChallengeService.VerifyAsync"/> opens a fresh connection per call, so two concurrent
    /// <c>VerifyAsync</c> calls for the same secret really do race at the database, not just in-process.
    /// <para>
    /// <b>Bare <c>Task.WhenAll</c> does not reliably force the two calls to overlap.</b> Confirmed
    /// directly: on this engine/environment, one call's entire round trip (open connection, select,
    /// hash-compare, consume) can complete before the other call's connection has even finished opening —
    /// the two never actually race at the database. <see cref="Internal.ChallengeService.VerifyAsync"/>'s
    /// fallback path for "nothing live" (<c>ClassifyMissingAsync</c>) does not distinguish an
    /// already-consumed row from one that never existed — both read as <see cref="ChallengeVerifyOutcome.NotFound"/> —
    /// so a non-overlapping second call reports <c>NotFound</c> instead of <see cref="ChallengeVerifyOutcome.Consumed"/>,
    /// which would make this test flaky (pass only when the incidental timing happens to overlap) rather
    /// than a real proof of <see cref="IChallengeDialect.ConsumeSql"/>'s atomicity. Gating
    /// <c>ConsumeSql</c> on a two-party <see cref="Barrier"/> — exactly
    /// <c>Themia.Challenges.Tests.RaceGatingChallengeDialect</c>'s technique, reused here over the real
    /// dialect instead of SQLite — guarantees both racers have already read the row as live before either
    /// is allowed to attempt the guarded <c>UPDATE</c>, which is what genuinely exercises the real
    /// engine's row-level locking rather than hoping thread-pool scheduling cooperates. See the task
    /// report for the separate, real defect this uncovered in <c>ClassifyMissingAsync</c>.
    /// </para>
    /// Run across several freshly issued challenges rather than once, to raise the odds of catching a
    /// timing-dependent regression in the atomicity itself.
    /// </summary>
    [Fact]
    public async Task TwoSimultaneousVerifications_ExactlyOneWins()
    {
        const int rounds = 10;
        for (var round = 0; round < rounds; round++)
        {
            var scope = new ChallengeScope(UniqueKey(), ChallengeEngineFixture.GenericPurpose, UniqueTenant());
            var issue = await fixture.Service.IssueAsync(scope);
            Assert.Equal(ChallengeIssueOutcome.Issued, issue.Outcome);

            using var barrier = new Barrier(2);
            var gatedDialect = new RaceGatingChallengeDialect(fixture.Dialect, barrier);
            var serviceA = new ChallengeService(gatedDialect, fixture.Options, fixture.TimeProvider, NullLogger<ChallengeService>.Instance);
            var serviceB = new ChallengeService(gatedDialect, fixture.Options, fixture.TimeProvider, NullLogger<ChallengeService>.Instance);

            var results = await Task.WhenAll(
                serviceA.VerifyAsync(scope, issue.Secret!),
                serviceB.VerifyAsync(scope, issue.Secret!));

            var outcomes = string.Join(", ", results.Select(r => r.Outcome));
            Assert.True(1 == results.Count(r => r.Outcome == ChallengeVerifyOutcome.Verified), $"round {round}: outcomes were [{outcomes}]");
            Assert.True(1 == results.Count(r => r.Outcome == ChallengeVerifyOutcome.Consumed), $"round {round}: outcomes were [{outcomes}]");
        }
    }

    /// <summary>
    /// The claim with the most hand-waving behind it (see each dialect's <c>IncrementWindowSql</c>
    /// remarks): <see cref="ConcurrencyLevel"/> parallel <see cref="IChallengeService.IssueAsync"/> calls
    /// for the exact same key must all succeed (the purpose's limits are configured far above
    /// <see cref="ConcurrencyLevel"/> — see <see cref="ChallengeEngineFixture.ConcurrencyPurpose"/>) and
    /// the persisted per-key ceiling counter must equal exactly <see cref="ConcurrencyLevel"/> — never
    /// fewer. A lost update here silently raises the real-world ceiling that bounds an SMS bill.
    /// <para>
    /// <b>MySQL only, confirmed:</b> at this concurrency level, InnoDB's gap locking on
    /// <c>challenge_rate_windows</c>' functional unique indexes (see
    /// <c>MySqlChallengeDialect.IncrementWindowSql</c>'s remarks) reliably raised
    /// <c>ER_LOCK_DEADLOCK</c> ("Deadlock found when trying to get lock") for one or more of the
    /// concurrent callers — reproduced deterministically across repeated runs. <c>ON DUPLICATE KEY
    /// UPDATE id = id</c> suppresses the benign <i>duplicate-key</i> error the same bucket collision
    /// would otherwise raise, but a deadlock is a different InnoDB failure mode that no SQL-text
    /// construct suppresses. This test originally carried its own bounded retry on MySQL error 1213 to
    /// keep the invariant assertion honest while flagging the gap rather than hiding it (see the task 7
    /// fix-round-2 report). <c>MySqlChallengeDialect.CreateConnection()</c> now returns a
    /// <c>DeadlockRetryingConnection</c> that retries exactly that error at the ADO.NET layer — see its
    /// remarks for why the fix belongs in the dialect, not <c>ChallengeService</c> — so this test calls
    /// <see cref="IChallengeService.IssueAsync"/> directly again: a bare call, no test-side retry, is now
    /// the actual proof the deadlock is handled rather than merely made rarer.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ConcurrentIssues_ForTheSameKey_ShouldNotLoseACount()
    {
        var key = UniqueKey();
        var tenantId = UniqueTenant();
        var scope = new ChallengeScope(key, ChallengeEngineFixture.ConcurrencyPurpose, tenantId);

        var results = await Task.WhenAll(Enumerable.Range(0, ConcurrencyLevel)
            .Select(_ => fixture.Service.IssueAsync(scope)));

        Assert.All(results, r => Assert.Equal(ChallengeIssueOutcome.Issued, r.Outcome));

        await using var connection = fixture.Dialect.CreateConnection();
        await connection.OpenAsync();
        // SUM rather than assuming a single row: the per-key ceiling is bucketed by window_start, and
        // while ConcurrencyPurpose's one-hour window makes a boundary crossing during a single test
        // vanishingly unlikely, SUM stays correct even if one occurred instead of silently mis-asserting.
        var keyCeilingCount = await connection.ExecuteScalarAsync<int>(
            $"SELECT COALESCE(SUM(count), 0) FROM challenge_rate_windows WHERE tenant_id = @TenantId AND {fixture.KeyColumn} = @Key AND purpose IS NULL",
            new { TenantId = tenantId, Key = key });

        Assert.Equal(ConcurrencyLevel, keyCeilingCount);
    }

    /// <summary>A fresh, collision-free scope key — see <see cref="ChallengeStoreTests.UniqueKey"/>.</summary>
    protected static string UniqueKey() => $"key-{Guid.NewGuid():N}";

    /// <summary>A fresh, collision-free tenant id.</summary>
    protected static string UniqueTenant() => $"tenant-{Guid.NewGuid():N}";

    /// <summary>PostgreSQL execution of <see cref="ConcurrencyTests"/>.</summary>
    [Collection(PostgresChallengesCollection.Name)]
    [Trait("Category", "Integration")]
    public sealed class PostgresConcurrencyTests : ConcurrencyTests
    {
        /// <summary>Creates the suite over the shared <see cref="PostgresChallengeFixture"/> container.</summary>
        public PostgresConcurrencyTests(PostgresChallengeFixture fixture) : base(fixture)
        {
        }
    }

    /// <summary>MySQL execution of <see cref="ConcurrencyTests"/>.</summary>
    [Collection(MySqlChallengesCollection.Name)]
    [Trait("Category", "Integration")]
    public sealed class MySqlConcurrencyTests : ConcurrencyTests
    {
        /// <summary>Creates the suite over the shared <see cref="MySqlChallengeFixture"/> container.</summary>
        public MySqlConcurrencyTests(MySqlChallengeFixture fixture) : base(fixture)
        {
        }
    }

    /// <summary>
    /// SQL Server execution of <see cref="ConcurrencyTests"/>, plus the one test pinning
    /// <c>SqlServerChallengeDialect.IncrementWindowSql</c>'s bespoke <c>SET XACT_ABORT OFF</c>/restore
    /// dance — engine-specific, so it belongs only here, not in the shared base.
    /// </summary>
    [Collection(SqlServerChallengesCollection.Name)]
    [Trait("Category", "Integration")]
    public sealed class SqlServerConcurrencyTests : ConcurrencyTests
    {
        private readonly SqlServerChallengeFixture sqlServerFixture;

        /// <summary>Creates the suite over the shared <see cref="SqlServerChallengeFixture"/> container.</summary>
        public SqlServerConcurrencyTests(SqlServerChallengeFixture fixture) : base(fixture) => sqlServerFixture = fixture;

        /// <summary>
        /// Pins, as a committed test, what <c>SqlServerChallengeDialect.IncrementWindowSql</c>'s remarks
        /// say was "verified once by hand against a live container": under an <b>ambient</b>
        /// <see cref="TransactionScope"/> — which makes <c>Microsoft.Data.SqlClient</c> set
        /// <c>XACT_ABORT ON</c> the moment the connection enlists — a benign duplicate-key collision on
        /// the rate-window seed <c>INSERT</c> must not doom the transaction. Forces the collision without
        /// needing a second connection: two <c>IncrementWindowSql</c> calls for the identical bucket on
        /// the <i>same</i> session collide immediately, because a unique index rejects a duplicate even
        /// against the same transaction's own uncommitted insert. "Prior work" is an ordinary challenge
        /// row inserted earlier in the same ambient transaction; both it and the final counter value must
        /// survive the commit.
        /// </summary>
        [Fact]
        public async Task AmbientTransactionWithXactAbortOn_SurvivesARateWindowSeedCollision()
        {
            var tenantId = UniqueTenant();
            var key = UniqueKey();
            var challengeId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var windowStart = new DateTimeOffset(now.Date, TimeSpan.Zero);

            using (var transactionScope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
            {
                await using var connection = sqlServerFixture.Dialect.CreateConnection();
                await connection.OpenAsync(); // auto-enlists in the ambient transaction; sets XACT_ABORT ON

                // "Prior work" that must survive the collision handled below.
                await connection.ExecuteAsync(sqlServerFixture.Dialect.InsertSql, new
                {
                    Id = challengeId,
                    TenantId = tenantId,
                    Key = key,
                    Purpose = ChallengeEngineFixture.GenericPurpose,
                    SecretHash = "test-hash",
                    SecretSalt = "test-salt",
                    TokenHash = (string?)null,
                    Attempts = 0,
                    ExpiresAt = now.AddMinutes(5),
                    CreatedAt = now,
                });

                // First call seeds the bucket (count 0) then updates it to 1.
                await connection.ExecuteAsync(sqlServerFixture.Dialect.IncrementWindowSql, new
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    Key = key,
                    Purpose = (string?)null,
                    WindowStart = windowStart,
                });

                // Second call, identical bucket, same session: the seed INSERT collides with the row this
                // same transaction just inserted — exactly the scenario the XACT_ABORT OFF/restore dance
                // exists for. Must not throw and must not doom the ambient transaction.
                var collision = await Record.ExceptionAsync(() => connection.ExecuteAsync(sqlServerFixture.Dialect.IncrementWindowSql, new
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    Key = key,
                    Purpose = (string?)null,
                    WindowStart = windowStart,
                }));
                Assert.Null(collision);

                transactionScope.Complete();
            }

            // Fresh connection outside any transaction: the ambient transaction must have committed.
            await using var verifyConnection = sqlServerFixture.Dialect.CreateConnection();
            await verifyConnection.OpenAsync();

            var challengeExists = await verifyConnection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM challenges WHERE id = @Id", new { Id = challengeId });
            Assert.Equal(1, challengeExists);

            var count = await verifyConnection.ExecuteScalarAsync<int>(
                $"SELECT count FROM challenge_rate_windows WHERE tenant_id = @TenantId AND {sqlServerFixture.KeyColumn} = @Key AND purpose IS NULL AND window_start = @WindowStart",
                new { TenantId = tenantId, Key = key, WindowStart = windowStart });
            Assert.Equal(2, count);
        }
    }
}
