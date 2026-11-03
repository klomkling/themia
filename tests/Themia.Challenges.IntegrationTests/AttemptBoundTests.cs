using Dapper;

using Microsoft.Extensions.Logging.Abstractions;

using Themia.Challenges.Internal;

using Xunit;

namespace Themia.Challenges.IntegrationTests;

/// <summary>
/// <see cref="PurposeOptions.MaxAttempts"/> holds under parallel guesses on the real engines. Bare parallelism is
/// timing-dependent (see <c>ConcurrencyTests</c>), so each proof forces its interleaving with
/// <see cref="StatementHookDialect"/>: a barrier before <see cref="IChallengeDialect.RecordAttemptSql"/> holds every
/// racer after it read the row, and a hook before <see cref="IChallengeDialect.ConsumeSql"/> spends the budget behind a
/// correct guess's back.
/// </summary>
public abstract class AttemptBoundTests
{
    private const int Max = 5;   // PurposeOptions.MaxAttempts default

    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(60);

    private readonly ChallengeEngineFixture fixture;

    /// <summary>Creates the suite over one engine's <paramref name="fixture"/>.</summary>
    protected AttemptBoundTests(ChallengeEngineFixture fixture) => this.fixture = fixture;

    /// <summary>The statements themselves: at the cap both refuse, one below the cap both accept.</summary>
    [Fact]
    public async Task TheStatements_RefuseARowThatHasSpentItsBudget()
    {
        var atCap = await IssueAsync(NewScope(ChallengeEngineFixture.GenericPurpose));
        await SetAttemptsAsync(atCap.Id, Max);
        var belowCap = await IssueAsync(NewScope(ChallengeEngineFixture.GenericPurpose));
        await SetAttemptsAsync(belowCap.Id, Max - 1);

        Assert.Equal(0, await ConsumeRowsAsync(atCap.Id));
        Assert.Equal(0, await RecordRowsAsync(atCap.Id));
        Assert.Equal(1, await RecordRowsAsync(belowCap.Id));
        Assert.Equal(Max, await AttemptsAsync(belowCap.Id));
        Assert.Equal(0, await ConsumeRowsAsync(belowCap.Id));

        var justUnder = await IssueAsync(NewScope(ChallengeEngineFixture.GenericPurpose));
        await SetAttemptsAsync(justUnder.Id, Max - 1);
        Assert.Equal(1, await ConsumeRowsAsync(justUnder.Id));
    }

    /// <summary>
    /// Eight wrong guesses all read the row before any is charged: exactly <see cref="Max"/> may be charged and
    /// answered Incorrect, the rest are refused. Pre-fix every one is charged (8 Incorrect, attempts 8).
    /// </summary>
    [Fact]
    public async Task EightGuessesThatAllReadBeforeAnyIsCharged_AreChargedAtMostMaxAttempts()
    {
        var scope = NewScope(ChallengeEngineFixture.GenericPurpose);
        var issued = await IssueAsync(scope);
        const int racers = 8;
        using var barrier = new Barrier(racers);
        var gated = new StatementHookDialect(fixture.Dialect, beforeConsume: null, beforeRecordAttempt: () => Wait(barrier));

        var results = await Task.WhenAll(Enumerable.Range(0, racers).Select(i => NewService(gated).VerifyAsync(scope, $"00000{i}")));

        var incorrect = results.Count(r => r.Outcome == ChallengeVerifyOutcome.Incorrect);
        var exhausted = results.Count(r => r.Outcome == ChallengeVerifyOutcome.AttemptsExhausted);
        var attempts = await AttemptsAsync(issued.Id);
        Assert.True(
            incorrect == Max && exhausted == racers - Max && attempts == Max,
            $"incorrect={incorrect} exhausted={exhausted} attempts={attempts}");
    }

    /// <summary>A correct guess judged against a stale read must not consume once the budget is spent behind its back.</summary>
    [Fact]
    public async Task ACorrectGuessWhoseBudgetIsSpentBeforeTheConsume_IsNotVerified()
    {
        var scope = NewScope(ChallengeEngineFixture.GenericPurpose);
        var issued = await IssueAsync(scope);
        var hooked = new StatementHookDialect(fixture.Dialect, beforeConsume: () => SetAttemptsAsync(issued.Id, Max).GetAwaiter().GetResult(), beforeRecordAttempt: null);

        var result = await NewService(hooked).VerifyAsync(scope, issued.Secret);

        Assert.Equal(ChallengeVerifyOutcome.AttemptsExhausted, result.Outcome);
        Assert.Equal(1, await LiveRowsAsync(issued.Id));
    }

    /// <summary>Same on a scope with siblings: not Verified, nothing consumed, no sibling voided.</summary>
    [Fact]
    public async Task ADeniedConsume_VoidsNoSibling()
    {
        var scope = NewScope(ChallengeEngineFixture.SiblingPurpose);
        var older = await IssueAsync(scope);
        var newer = await IssueAsync(scope);
        var hooked = new StatementHookDialect(fixture.Dialect, beforeConsume: () => SetAttemptsAsync(newer.Id, Max).GetAwaiter().GetResult(), beforeRecordAttempt: null);

        var result = await NewService(hooked).VerifyAsync(scope, newer.Secret);

        Assert.NotEqual(ChallengeVerifyOutcome.Verified, result.Outcome);
        Assert.Equal(1, await LiveRowsAsync(newer.Id));
        Assert.Equal(1, await LiveRowsAsync(older.Id));
    }

    /// <summary>The magic-link path has the same stale read between its attempts check and its consume.</summary>
    [Fact]
    public async Task ATokenWhoseBudgetIsSpentBeforeTheConsume_IsNotVerified()
    {
        var scope = NewScope(ChallengeEngineFixture.SiblingTokenPurpose);
        var other = await IssueAsync(scope);
        var target = await IssueAsync(scope);
        var hooked = new StatementHookDialect(fixture.Dialect, beforeConsume: () => SetAttemptsAsync(target.Id, Max).GetAwaiter().GetResult(), beforeRecordAttempt: null);

        var result = await NewService(hooked).VerifyByTokenAsync(target.Secret, scope.Purpose, scope.TenantId);

        Assert.Equal(ChallengeVerifyOutcome.AttemptsExhausted, result.Outcome);
        Assert.Equal(1, await LiveRowsAsync(target.Id));
        Assert.Equal(1, await LiveRowsAsync(other.Id));
    }

    /// <summary>After the budget is spent by wrong guesses the right code is refused, peek or verify.</summary>
    [Fact]
    public async Task OnceTheBudgetIsSpent_TheRightCodeIsRefused()
    {
        var scope = NewScope(ChallengeEngineFixture.GenericPurpose);
        var issued = await IssueAsync(scope);
        for (var i = 0; i < Max; i++)
        {
            await fixture.Service.VerifyAsync(scope, $"11111{i}");
        }

        Assert.Equal(ChallengeVerifyOutcome.AttemptsExhausted, (await fixture.Service.VerifyAsync(scope, issued.Secret)).Outcome);
        Assert.NotEqual(ChallengeVerifyOutcome.Verified, (await fixture.Service.PeekAsync(scope, issued.Secret)).Outcome);
    }

    /// <summary>An extra, unforced burst: the final count never exceeds the cap whatever the interleaving.</summary>
    [Fact]
    public async Task AParallelBurst_NeverChargesPastTheCap()
    {
        var scope = NewScope(ChallengeEngineFixture.GenericPurpose);
        var issued = await IssueAsync(scope);
        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 16).Select(i => Task.Run(async () =>
        {
            start.Wait();
            return await fixture.Service.VerifyAsync(scope, $"22222{i}");
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(tasks);

        Assert.True(await AttemptsAsync(issued.Id) <= Max, $"attempts={await AttemptsAsync(issued.Id)}");
        Assert.True(results.Count(r => r.Outcome == ChallengeVerifyOutcome.Incorrect) <= Max);
    }

    private static void Wait(Barrier barrier)
    {
        if (!barrier.SignalAndWait(GateTimeout))
        {
            throw new InvalidOperationException("The racing guesses never all reached the charge; the run would have been sequential.");
        }
    }

    private ChallengeService NewService(IChallengeDialect dialect) =>
        new(dialect, fixture.Options, fixture.TimeProvider, NullLogger<ChallengeService>.Instance);

    private async Task<(Guid Id, string Secret)> IssueAsync(ChallengeScope scope)
    {
        var issued = await fixture.Service.IssueAsync(scope);
        Assert.Equal(ChallengeIssueOutcome.Issued, issued.Outcome);
        return (issued.ChallengeId!.Value, issued.Secret!);
    }

    private static ChallengeScope NewScope(string purpose) => new($"key-{Guid.NewGuid():N}", purpose, $"tenant-{Guid.NewGuid():N}");

    private async Task SetAttemptsAsync(Guid id, int attempts)
    {
        await using var connection = fixture.Dialect.CreateConnection();
        await connection.ExecuteAsync("UPDATE challenges SET attempts = @Attempts WHERE id = @Id;", new { Id = id, Attempts = attempts });
    }

    private async Task<int> AttemptsAsync(Guid id)
    {
        await using var connection = fixture.Dialect.CreateConnection();
        return await connection.ExecuteScalarAsync<int>("SELECT attempts FROM challenges WHERE id = @Id;", new { Id = id });
    }

    private async Task<long> LiveRowsAsync(Guid id)
    {
        await using var connection = fixture.Dialect.CreateConnection();
        return Convert.ToInt64(await connection.ExecuteScalarAsync<object>("SELECT COUNT(*) FROM challenges WHERE id = @Id AND consumed_at IS NULL;", new { Id = id }));
    }

    private async Task<int> ConsumeRowsAsync(Guid id)
    {
        await using var connection = fixture.Dialect.CreateConnection();
        var now = fixture.TimeProvider.GetUtcNow();
        return await connection.ExecuteAsync(fixture.Dialect.ConsumeSql, new { Id = id, Now = now, ConsumedAt = now, MaxAttempts = Max });
    }

    private async Task<int> RecordRowsAsync(Guid id)
    {
        await using var connection = fixture.Dialect.CreateConnection();
        return await connection.ExecuteAsync(fixture.Dialect.RecordAttemptSql, new { Id = id, MaxAttempts = Max });
    }

    /// <summary>PostgreSQL execution of <see cref="AttemptBoundTests"/>.</summary>
    [Collection(PostgresChallengesCollection.Name)]
    [Trait("Category", "Integration")]
    public sealed class PostgresAttemptBoundTests : AttemptBoundTests
    {
        /// <summary>Creates the suite over the shared <see cref="PostgresChallengeFixture"/> container.</summary>
        public PostgresAttemptBoundTests(PostgresChallengeFixture fixture) : base(fixture)
        {
        }
    }

    /// <summary>MySQL execution of <see cref="AttemptBoundTests"/>.</summary>
    [Collection(MySqlChallengesCollection.Name)]
    [Trait("Category", "Integration")]
    public sealed class MySqlAttemptBoundTests : AttemptBoundTests
    {
        /// <summary>Creates the suite over the shared <see cref="MySqlChallengeFixture"/> container.</summary>
        public MySqlAttemptBoundTests(MySqlChallengeFixture fixture) : base(fixture)
        {
        }
    }

    /// <summary>SQL Server execution of <see cref="AttemptBoundTests"/>.</summary>
    [Collection(SqlServerChallengesCollection.Name)]
    [Trait("Category", "Integration")]
    public sealed class SqlServerAttemptBoundTests : AttemptBoundTests
    {
        /// <summary>Creates the suite over the shared <see cref="SqlServerChallengeFixture"/> container.</summary>
        public SqlServerAttemptBoundTests(SqlServerChallengeFixture fixture) : base(fixture)
        {
        }
    }
}
