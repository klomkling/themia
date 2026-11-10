using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Themia.Exceptional.Alerts;
using Xunit;

namespace Themia.Exceptional.Conformance;

/// <summary>
/// Provider-agnostic contract for the alert SQL (candidate triggers, claim, reclaim, fencing, cap count, cleanup)
/// run against a live, migrated database of each engine. Time is controlled by seeding rows with explicit dates and
/// by passing a negative lease, which makes every claim immediately stale without engine-specific date SQL.
/// </summary>
public abstract class ExceptionAlertStoreConformanceTests : IAsyncLifetime
{
    private const string App = "App";
    private const long AlertMigrationVersion = 202611100002;
    private const int RaceRounds = 20;
    private static readonly TimeSpan EveryClaimIsStale = TimeSpan.FromSeconds(-60);

    private DapperExceptionAlertStore? alertStore;

    /// <summary>Alert dialect over the live database.</summary>
    protected abstract IExceptionAlertSqlDialect AlertDialect { get; }

    /// <summary>Exception store over the same database (used to seed rows).</summary>
    protected abstract IExceptionStore ExceptionStore { get; }

    /// <summary>Quotes an identifier for the engine.</summary>
    protected abstract string Quote(string name);

    /// <summary>Runs the Exceptional migrations against the test database.</summary>
    protected abstract void RunMigrations();

    /// <summary>The migration ledger table of the Exceptional assembly.</summary>
    protected abstract string VersionTableName { get; }

    /// <summary>Statement that drops the unique (ExceptionId, AlertTrigger) index of the state table.</summary>
    protected abstract string DropUniqueIndexSql { get; }

    private DapperExceptionAlertStore AlertStore => alertStore ??= new DapperExceptionAlertStore(AlertDialect);

    /// <inheritdoc />
    public virtual async Task InitializeAsync()
    {
        await using DbConnection connection = AlertDialect.CreateConnection();
        await connection.OpenAsync();
        foreach (var table in new[] { "ExceptionAlertState", "Exceptions" })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM {Quote(table)}";
            await command.ExecuteNonQueryAsync();
        }
    }

    /// <inheritdoc />
    public virtual Task DisposeAsync() => Task.CompletedTask;

    private static ExceptionAlertOptions Options() => new();

    private async Task<ExceptionEntry> SeedAsync(
        string hash, TimeSpan createdAgo, TimeSpan lastLogAgo, int count = 1, string app = App)
    {
        var now = DateTime.UtcNow;
        var entry = new ExceptionEntry
        {
            Guid = Guid.NewGuid(), ApplicationName = app, MachineName = "M", Type = "System.Exception",
            Message = "boom", Detail = "{}", ErrorHash = hash, DuplicateCount = count,
            CreationDate = now - createdAgo, LastLogDate = now - lastLogAgo,
        };
        await ExceptionStore.LogAsync(entry);
        var stored = await ExceptionStore.GetAsync(entry.Guid);
        return stored!;
    }

    private async Task<IReadOnlyList<ExceptionAlertCandidate>> CandidatesAsync(string app = App) =>
        await AlertStore.GetCandidatesAsync(app, Options(), 100, default);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Candidates_ReportAFirstSeenHashAsNew()
    {
        var row = await SeedAsync("h1", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1));

        var candidates = await CandidatesAsync();

        var candidate = Assert.Single(candidates);
        Assert.Equal(ExceptionAlertTrigger.New, candidate.Trigger);
        Assert.Equal(row.Id, candidate.ExceptionId);
        Assert.Equal(row.Guid, candidate.Guid);
        Assert.Equal(DateTimeKind.Utc, candidate.LastSeenUtc.Kind);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Candidates_IgnoreOtherApplicationsSoftDeletedRowsAndRowsOutsideTheLookback()
    {
        await SeedAsync("other-app", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), app: "Other");
        var deleted = await SeedAsync("deleted", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1));
        await ExceptionStore.DeleteAsync(deleted.Guid);
        await SeedAsync("old", TimeSpan.FromHours(5), TimeSpan.FromHours(3));

        Assert.Empty(await CandidatesAsync());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Candidates_ReportAHashQuietLongerThanThePeriodAsReappeared()
    {
        await SeedAsync("back", TimeSpan.FromDays(3), TimeSpan.FromDays(2));
        var current = await SeedAsync("back", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1));

        var candidate = Assert.Single(await CandidatesAsync());

        Assert.Equal(ExceptionAlertTrigger.Reappeared, candidate.Trigger);
        Assert.Equal(current.Id, candidate.ExceptionId);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Candidates_DoNotReportAHashThatWasSeenWithinTheQuietPeriod()
    {
        await SeedAsync("steady", TimeSpan.FromHours(10), TimeSpan.FromHours(5));
        await SeedAsync("steady", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1));

        Assert.Empty(await CandidatesAsync());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Candidates_DoNotTreatASoftDeletedEarlierRowAsNew()
    {
        var earlier = await SeedAsync("history", TimeSpan.FromHours(10), TimeSpan.FromHours(5));
        await ExceptionStore.DeleteAsync(earlier.Guid);
        await SeedAsync("history", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1));

        Assert.Empty(await CandidatesAsync());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Candidates_ReportACountAtTheThresholdOnce_AndSuppressItWhileAThresholdAlertIsRecent()
    {
        await SeedAsync("noisy", TimeSpan.FromHours(10), TimeSpan.FromHours(5));
        var first = await SeedAsync("noisy", TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(20), count: 60);

        var candidate = Assert.Single(await CandidatesAsync());
        Assert.Equal(ExceptionAlertTrigger.Threshold, candidate.Trigger);
        Assert.Equal(first.Id, candidate.ExceptionId);

        Assert.True(await AlertStore.TryClaimAsync(candidate, Guid.NewGuid(), default));
        await SeedAsync("noisy", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), count: 70);

        Assert.Empty(await CandidatesAsync());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Claim_RemovesTheRowFromTheCandidatesForThatTrigger()
    {
        await SeedAsync("h1", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1));
        var candidate = Assert.Single(await CandidatesAsync());

        Assert.True(await AlertStore.TryClaimAsync(candidate, Guid.NewGuid(), default));

        Assert.Empty(await CandidatesAsync());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Claim_IsWonByExactlyOneOfTwoRacingInstances()
    {
        for (var round = 0; round < RaceRounds; round++)
        {
            await InitializeAsync();
            await SeedAsync("race" + round, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1));
            var candidate = Assert.Single(await CandidatesAsync());
            using var start = new ManualResetEventSlim();

            var attempts = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
            {
                start.Wait();
                return await AlertStore.TryClaimAsync(candidate, Guid.NewGuid(), default);
            })).ToArray();
            start.Set();
            var results = await Task.WhenAll(attempts);

            Assert.Equal(1, results.Count(won => won));
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Service_SendsEachAlertOnce_WhenTwoInstancesRunTogether()
    {
        for (var round = 0; round < RaceRounds; round++)
        {
            await InitializeAsync();
            for (var i = 0; i < 3; i++)
                await SeedAsync($"svc{round}-{i}", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1));
            var channel = new RecordingChannel();
            using var start = new ManualResetEventSlim();

            var runs = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
            {
                start.Wait();
                var options = new ExceptionAlertOptions { EnvironmentName = "Test", AdminUrlTemplate = "https://x.test/{guid}" };
                await new ExceptionAlertService(AlertStore, channel, options, App, NullLogger<ExceptionAlertService>.Instance)
                    .RunOnceAsync(default);
            })).ToArray();
            start.Set();
            await Task.WhenAll(runs);

            Assert.Equal(3, channel.IncludedTotal);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Reclaim_IsWonByOneInstance_AndFencesTheOriginalBatch()
    {
        await SeedAsync("h1", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1));
        var candidate = Assert.Single(await CandidatesAsync());
        var original = Guid.NewGuid();
        var takeover = Guid.NewGuid();
        Assert.True(await AlertStore.TryClaimAsync(candidate, original, default));

        Assert.Equal(original, await AlertStore.FindStaleBatchAsync(App, EveryClaimIsStale, 5, default));
        Assert.True(await AlertStore.TryReclaimAsync(App, original, takeover, EveryClaimIsStale, default));
        Assert.False(await AlertStore.TryReclaimAsync(App, original, Guid.NewGuid(), EveryClaimIsStale, default));

        Assert.Equal(0, await AlertStore.MarkSentAsync(original, default));
        Assert.Equal(0, await AlertStore.MarkFailedAsync(original, default));
        Assert.Single(await AlertStore.GetClaimedAsync(App, takeover, default));
        Assert.Equal(1, await AlertStore.MarkSentAsync(takeover, default));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MarkSent_CountsTheBatchTowardsTheHourlyCap_AndASentBatchIsNotReclaimed()
    {
        await SeedAsync("h1", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1));
        var batch = Guid.NewGuid();
        await AlertStore.TryClaimAsync(Assert.Single(await CandidatesAsync()), batch, default);

        Assert.Equal(0, await AlertStore.CountSentBatchesLastHourAsync(App, default));
        Assert.Equal(1, await AlertStore.MarkSentAsync(batch, default));

        Assert.Equal(1, await AlertStore.CountSentBatchesLastHourAsync(App, default));
        Assert.Null(await AlertStore.FindStaleBatchAsync(App, EveryClaimIsStale, 5, default));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FailExhausted_MarksOnlyStaleClaimsThatUsedUpTheirAttempts()
    {
        await SeedAsync("h1", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1));
        await AlertStore.TryClaimAsync(Assert.Single(await CandidatesAsync()), Guid.NewGuid(), default);

        Assert.Equal(0, await AlertStore.FailExhaustedAsync(App, EveryClaimIsStale, maxAttempts: 2, default));
        Assert.Equal(1, await AlertStore.FailExhaustedAsync(App, EveryClaimIsStale, maxAttempts: 1, default));
        Assert.Null(await AlertStore.FindStaleBatchAsync(App, EveryClaimIsStale, 1, default));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Cleanup_DeletesFinishedRowsButKeepsClaimedOnes()
    {
        await SeedAsync("sent", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1));
        await SeedAsync("held", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1));
        var candidates = await CandidatesAsync();
        var sent = Guid.NewGuid();
        await AlertStore.TryClaimAsync(candidates.First(c => c.ErrorHash == "sent"), sent, default);
        await AlertStore.TryClaimAsync(candidates.First(c => c.ErrorHash == "held"), Guid.NewGuid(), default);
        await AlertStore.MarkSentAsync(sent, default);

        Assert.Equal(1, await AlertStore.CleanupAsync(App, EveryClaimIsStale, default));
        Assert.NotNull(await AlertStore.FindStaleBatchAsync(App, EveryClaimIsStale, 5, default));
    }

    private sealed class RecordingChannel : IAlertChannel
    {
        private int includedTotal;

        public int IncludedTotal => Volatile.Read(ref includedTotal);

        public Task<AlertSendOutcome> SendAsync(AlertDigest digest, CancellationToken cancellationToken)
        {
            Interlocked.Add(ref includedTotal, digest.IncludedCount);
            return Task.FromResult(AlertSendOutcome.Sent);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task StateStatements_AreScopedToTheirApplication()
    {
        const string other = "Other";
        await SeedAsync("h1", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1));
        var batch = Guid.NewGuid();
        await AlertStore.TryClaimAsync(Assert.Single(await CandidatesAsync()), batch, default);

        Assert.Null(await AlertStore.FindStaleBatchAsync(other, EveryClaimIsStale, 5, default));
        Assert.False(await AlertStore.TryReclaimAsync(other, batch, Guid.NewGuid(), EveryClaimIsStale, default));
        Assert.Empty(await AlertStore.GetClaimedAsync(other, batch, default));
        Assert.Equal(0, await AlertStore.FailExhaustedAsync(other, EveryClaimIsStale, maxAttempts: 1, default));

        await AlertStore.MarkSentAsync(batch, default);
        Assert.Equal(0, await AlertStore.CountSentBatchesLastHourAsync(other, default));
        Assert.Equal(0, await AlertStore.CleanupAsync(other, EveryClaimIsStale, default));
        Assert.Equal(1, await AlertStore.CountSentBatchesLastHourAsync(App, default));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Migration_RecreatesAMissingUniqueIndex_SoAHalfAppliedRunIsRepairedOnReplay()
    {
        await using (DbConnection connection = AlertDialect.CreateConnection())
        {
            await connection.OpenAsync();
            await using var drop = connection.CreateCommand();
            drop.CommandText = DropUniqueIndexSql;
            await drop.ExecuteNonQueryAsync();

            // Un-record the migration so the runner applies it again, as it would after a run that died part-way.
            await using var forget = connection.CreateCommand();
            forget.CommandText = $"DELETE FROM {Quote(VersionTableName)} WHERE {Quote("Version")} = {AlertMigrationVersion}";
            Assert.Equal(1, await forget.ExecuteNonQueryAsync());
        }

        RunMigrations();

        await SeedAsync("h1", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1));
        var candidate = Assert.Single(await CandidatesAsync());
        Assert.True(await AlertStore.TryClaimAsync(candidate, Guid.NewGuid(), default));
        Assert.False(await AlertStore.TryClaimAsync(candidate, Guid.NewGuid(), default));
    }
}
