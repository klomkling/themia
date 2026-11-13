using Microsoft.Extensions.Logging.Abstractions;
using Themia.Exceptional.Alerts;
using Xunit;

namespace Themia.Exceptional.Tests;

public class ExceptionAlertServiceTests
{
    private static readonly DateTime Seen = new(2026, 11, 10, 5, 0, 0, DateTimeKind.Utc);

    private static ExceptionAlertOptions Options(int maxPerHour = 12) => new()
    {
        Enabled = true,
        BotToken = "123456:secret-token-value",
        ChatId = "-1001",
        EnvironmentName = "Production",
        AdminUrlTemplate = "https://admin.example.test/exceptions/{guid}",
        MaxMessagesPerHour = maxPerHour,
    };

    private static ExceptionAlertCandidate Candidate(
        long id, ExceptionAlertTrigger trigger = ExceptionAlertTrigger.New, string message = "boom") => new()
    {
        ExceptionId = id,
        Guid = Guid.NewGuid(),
        ApplicationName = "App",
        ErrorHash = "h" + id,
        Trigger = trigger,
        Type = "System.InvalidOperationException",
        Message = message,
        DuplicateCount = 3,
        FirstSeenUtc = Seen,
        LastSeenUtc = Seen.AddMinutes(2),
    };

    private static ExceptionAlertService Service(FakeStore store, FakeChannel channel, ExceptionAlertOptions? options = null) =>
        new(store, channel, options ?? Options(), "App", NullLogger<ExceptionAlertService>.Instance);

    [Theory]
    [InlineData(ExceptionAlertTrigger.New)]
    [InlineData(ExceptionAlertTrigger.Reappeared)]
    [InlineData(ExceptionAlertTrigger.Threshold)]
    public async Task RunOnce_SendsOneDigestAndMarksTheBatchSent_ForEachTrigger(ExceptionAlertTrigger trigger)
    {
        var store = new FakeStore { Candidates = { Candidate(1, trigger) } };
        var channel = new FakeChannel();

        await Service(store, channel).RunOnceAsync(default);

        Assert.Single(channel.Sent);
        Assert.Contains(trigger.ToString().ToUpperInvariant(), channel.Sent[0].Text, StringComparison.Ordinal);
        Assert.Equal(1, store.SentBatches);
    }

    [Fact]
    public async Task RunOnce_SendsASingleDigestForSeveralGroups()
    {
        var store = new FakeStore { Candidates = { Candidate(1), Candidate(2), Candidate(3) } };
        var channel = new FakeChannel();

        await Service(store, channel).RunOnceAsync(default);

        Assert.Single(channel.Sent);
        Assert.Equal(3, store.Claims.Count);
    }

    [Fact]
    public async Task RunOnce_AlertsARowOnceUnderTheLowestTrigger_WhenItQualifiesForSeveral()
    {
        var store = new FakeStore { Candidates = { Candidate(1, ExceptionAlertTrigger.Threshold), Candidate(1, ExceptionAlertTrigger.New) } };

        await Service(store, new FakeChannel()).RunOnceAsync(default);

        Assert.Equal([(1L, ExceptionAlertTrigger.New)], store.Claims.Select(c => (c.Key.Item1, c.Key.Item2)));
    }

    [Fact]
    public async Task RunOnce_ClaimsAndSendsNothing_WhenTheHourlyCapIsReached()
    {
        var store = new FakeStore { Candidates = { Candidate(1) }, SentBatches = 2 };
        var channel = new FakeChannel();

        await Service(store, channel, Options(maxPerHour: 2)).RunOnceAsync(default);

        Assert.Empty(channel.Sent);
        Assert.Empty(store.Claims);
    }

    [Fact]
    public async Task RunOnce_OnlyClaimsGroupsThatFitInTheMessage()
    {
        var options = Options();
        options.AdminUrlTemplate = "https://admin.example.test/" + new string('p', 400) + "/{guid}";
        var candidates = Enumerable.Range(1, 15).Select(i => Candidate(i)).ToList();
        var store = new FakeStore();
        store.Candidates.AddRange(candidates);
        var channel = new FakeChannel();

        await Service(store, channel, options).RunOnceAsync(default);

        Assert.InRange(channel.Sent[0].Text.Length, 1, AlertDigestBuilder.MaxMessageLength);
        Assert.Equal(channel.Sent[0].IncludedCount, store.Claims.Count);
        Assert.True(store.Claims.Count < 15);
    }

    [Fact]
    public async Task RunOnce_SendsNothing_WhenAnotherInstanceWonEveryClaim()
    {
        var store = new FakeStore { Candidates = { Candidate(1) } };
        store.Claims[(1, ExceptionAlertTrigger.New)] = Guid.NewGuid();
        var channel = new FakeChannel();

        await Service(store, channel).RunOnceAsync(default);

        Assert.Empty(channel.Sent);
    }

    [Fact]
    public async Task RunOnce_TwoInstancesOnOneStore_SendEachAlertOnce()
    {
        var store = new FakeStore { Candidates = { Candidate(1), Candidate(2) } };
        var channel = new FakeChannel();

        await Task.WhenAll(
            Service(store, channel).RunOnceAsync(default),
            Service(store, channel).RunOnceAsync(default));

        Assert.Equal(2, channel.Sent.Sum(d => d.IncludedCount));
        Assert.Equal(2, store.Claims.Count);
    }

    [Fact]
    public async Task RunOnce_LeavesTheBatchClaimed_WhenTheSendIsTransient()
    {
        var store = new FakeStore { Candidates = { Candidate(1) } };
        var channel = new FakeChannel { Outcome = AlertSendOutcome.Transient };

        await Service(store, channel).RunOnceAsync(default);

        Assert.Equal(0, store.SentBatches);
        Assert.Equal(0, store.FailedBatches);
    }

    [Fact]
    public async Task RunOnce_MarksTheBatchFailed_WhenTheSendIsPermanent()
    {
        var store = new FakeStore { Candidates = { Candidate(1) } };
        var channel = new FakeChannel { Outcome = AlertSendOutcome.Permanent };

        await Service(store, channel).RunOnceAsync(default);

        Assert.Equal(1, store.FailedBatches);
    }

    [Fact]
    public async Task RunOnce_ResendsAStaleBatchBeforeLookingForNewAlerts()
    {
        var stale = Guid.NewGuid();
        var store = new FakeStore { Candidates = { Candidate(2) }, StaleBatch = stale };
        store.BatchRows[stale] = [Candidate(1)];
        var channel = new FakeChannel();

        await Service(store, channel).RunOnceAsync(default);

        Assert.Single(channel.Sent);
        Assert.Empty(store.Claims);
        Assert.Equal(1, store.SentBatches);
    }

    [Fact]
    public async Task RunOnce_SkipsAStaleBatch_WhenAnotherInstanceReclaimedItFirst()
    {
        var store = new FakeStore { StaleBatch = Guid.NewGuid(), ReclaimWins = false };
        var channel = new FakeChannel();

        await Service(store, channel).RunOnceAsync(default);

        Assert.Empty(channel.Sent);
    }

    [Fact]
    public async Task RunOnce_LogsOneError_WhenAlertsWereAbandonedAfterTheirAttempts()
    {
        var store = new FakeStore { Exhausted = 3 };
        var logger = new ListLogger();

        await new ExceptionAlertService(store, new FakeChannel(), Options(), "App", logger).RunOnceAsync(default);

        var error = Assert.Single(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error);
        Assert.Contains("3", error.Message);
    }

    [Fact]
    public async Task RunOnce_OmitsTheExceptionMessageAndEverythingElse()
    {
        var store = new FakeStore();
        store.Candidates.Add(Candidate(1, message: "user jane.doe@example.test phone 0812345678 failed"));
        var channel = new FakeChannel();

        await Service(store, channel).RunOnceAsync(default);

        var text = Assert.Single(channel.Sent).Text;
        Assert.DoesNotContain("jane.doe@example.test", text);
        Assert.DoesNotContain("0812345678", text);
        Assert.DoesNotContain("failed", text);
    }

    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger<ExceptionAlertService>
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class FakeChannel : IAlertChannel
    {
        public List<AlertDigest> Sent { get; } = [];
        public AlertSendOutcome Outcome { get; set; } = AlertSendOutcome.Sent;

        public Task<AlertSendOutcome> SendAsync(AlertDigest digest, CancellationToken cancellationToken)
        {
            lock (Sent)
                Sent.Add(digest);
            return Task.FromResult(Outcome);
        }
    }

    private sealed class FakeStore : IExceptionAlertStore
    {
        private readonly object gate = new();

        public List<ExceptionAlertCandidate> Candidates { get; } = [];
        public Dictionary<(long, ExceptionAlertTrigger), Guid> Claims { get; } = [];
        public Dictionary<Guid, IReadOnlyList<ExceptionAlertCandidate>> BatchRows { get; } = [];
        public int SentBatches { get; set; }
        public int FailedBatches { get; private set; }
        public Guid? StaleBatch { get; set; }
        public bool ReclaimWins { get; set; } = true;

        public Task<IReadOnlyList<ExceptionAlertCandidate>> GetCandidatesAsync(
            string applicationName, ExceptionAlertOptions options, int limit, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                IReadOnlyList<ExceptionAlertCandidate> open = Candidates
                    .Where(c => !Claims.ContainsKey((c.ExceptionId, c.Trigger))).Take(limit).ToList();
                return Task.FromResult(open);
            }
        }

        public Task<bool> TryClaimAsync(ExceptionAlertCandidate candidate, Guid batchId, CancellationToken cancellationToken)
        {
            lock (gate)
                return Task.FromResult(Claims.TryAdd((candidate.ExceptionId, candidate.Trigger), batchId));
        }

        public Task<IReadOnlyList<ExceptionAlertCandidate>> GetClaimedAsync(string applicationName, Guid batchId, CancellationToken cancellationToken) =>
            Task.FromResult(BatchRows.GetValueOrDefault(batchId) ?? []);

        public Task<Guid?> FindStaleBatchAsync(string applicationName, TimeSpan lease, int maxAttempts, CancellationToken cancellationToken) =>
            Task.FromResult(StaleBatch);

        public Task<bool> TryReclaimAsync(string applicationName, Guid oldBatchId, Guid newBatchId, TimeSpan lease, CancellationToken cancellationToken)
        {
            if (ReclaimWins && BatchRows.TryGetValue(oldBatchId, out var rows))
                BatchRows[newBatchId] = rows;
            return Task.FromResult(ReclaimWins);
        }

        public int Exhausted { get; set; }

        public Task<int> FailExhaustedAsync(string applicationName, TimeSpan lease, int maxAttempts, CancellationToken cancellationToken) => Task.FromResult(Exhausted);

        public Task<int> MarkSentAsync(Guid batchId, CancellationToken cancellationToken)
        {
            lock (gate)
                SentBatches++;
            return Task.FromResult(1);
        }

        public Task<int> MarkFailedAsync(Guid batchId, CancellationToken cancellationToken)
        {
            FailedBatches++;
            return Task.FromResult(1);
        }

        public Task<int> CountSentBatchesLastHourAsync(string applicationName, CancellationToken cancellationToken) => Task.FromResult(SentBatches);

        public Task<int> CleanupAsync(string applicationName, TimeSpan retention, CancellationToken cancellationToken) => Task.FromResult(0);
    }
}
