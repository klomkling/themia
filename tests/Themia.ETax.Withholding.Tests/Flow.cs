using Xunit;
using System.Data.Common;
using Microsoft.Extensions.Logging;

namespace Themia.ETax.Withholding.Tests;

/// <summary>The in-memory store, a counting number source, a clock and a capturing logger, wired the way an adopter wires them.</summary>
internal sealed class Flow
{
    public const string Payer = "payer-1";

    public Flow(WithholdingValidationOptions? validation = null)
    {
        Service = new WithholdingCertificateService(
            Store, Numbers, Samples.Catalogue(), validation ?? new WithholdingValidationOptions(), Clock, Logs);
    }

    public InMemoryWithholdingCertificateStore Store { get; } = new();

    public CountingNumbers Numbers { get; } = new();

    public CapturingLogger<WithholdingCertificateService> Logs { get; } = new();

    public TimeProvider Clock { get; } = new FixedClock(new DateTimeOffset(2026, 10, 15, 3, 0, 0, TimeSpan.Zero));

    public WithholdingCertificateService Service { get; }

    public static IssueWithholdingCertificateRequest Request(
        string source, WithholdingCertificateSnapshot? snapshot = null, int revision = 1, string payer = Payer) => new()
    {
        Key = new WithholdingCertificateKey(source, revision),
        PayerId = payer,
        Snapshot = snapshot ?? Samples.Valid(),
    };

    /// <summary>Runs <see cref="WithholdingCertificateService.IssueAsync"/> in its own transaction: committed when issued or already issued, else rolled back.</summary>
    public async Task<WithholdingIssueResult> IssueAsync(IssueWithholdingCertificateRequest request)
    {
        await using var tx = new InMemoryWithholdingCertificateStore.FakeTransaction();
        var result = await Service.IssueAsync(tx, request);
        if (result.Outcome is WithholdingIssueOutcome.Issued or WithholdingIssueOutcome.AlreadyIssued)
            await tx.CommitAsync();
        else
            await tx.RollbackAsync();
        return result;
    }

    public async Task<Guid> IssuedAsync(string source, WithholdingCertificateSnapshot? snapshot = null)
    {
        var result = await IssueAsync(Request(source, snapshot));
        Assert.Equal(WithholdingIssueOutcome.Issued, result.Outcome);
        return result.DocumentId!.Value;
    }

    public WithholdingCertificateRecord Row(Guid id) => Store.Rows.Single(r => r.Id == id);

    /// <summary>A number source that counts its calls and gives the number back when the transaction rolls back.</summary>
    internal sealed class CountingNumbers : IWithholdingNumberSource
    {
        private int _last;

        public int Calls { get; private set; }

        /// <summary>Runs when a number is requested, i.e. between the first validation and the store.</summary>
        public Action? OnNext { get; set; }

        public Task<string> NextNumberAsync(DbTransaction tx, string payerId, DateOnly issueDate, CancellationToken ct = default)
        {
            Calls++;
            OnNext?.Invoke();
            var number = ++_last;
            ((InMemoryWithholdingCertificateStore.FakeTransaction)tx).OnRollback(() => _last--);
            return Task.FromResult($"WHT-{issueDate.Year + 543}-{number:D6}");
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

/// <summary>Records every formatted message and structured value, so a test can prove none of them carries personal data.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<string> _entries = [];

    public IReadOnlyList<string> Entries => _entries;

    public string AllText => string.Join("\n", _entries);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var text = formatter(state, exception);
        if (state is IEnumerable<KeyValuePair<string, object?>> values)
            text += " " + string.Join(" ", values.Select(v => $"{v.Key}={v.Value}"));
        _entries.Add(text + (exception is null ? string.Empty : " " + exception));
    }
}
