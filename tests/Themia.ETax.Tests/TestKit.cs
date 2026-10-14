using Xunit;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Themia.Pdf;

namespace Themia.ETax.Tests;

internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

internal sealed class LogSink
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get { lock (_entries) { return _entries.ToList(); } }
    }

    public string AllText => string.Join("\n", Entries.Select(e => e.Message));

    public void Add(LogLevel level, string message)
    {
        lock (_entries) { _entries.Add((level, message)); }
    }
}

internal sealed class CapturingLogger<T>(LogSink sink) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        // Everything a real sink could render: the message, every structured value, and the full exception text.
        var text = new StringBuilder(formatter(state, exception));
        if (state is IEnumerable<KeyValuePair<string, object?>> values)
            foreach (var (key, value) in values)
                text.Append(' ').Append(key).Append('=').Append(value);
        if (exception is not null)
            text.Append(' ').Append(exception);
        sink.Add(logLevel, text.ToString());
    }
}

/// <summary>A fake Chromium: returns a PDF whose body is a hash of the HTML, so the same input gives the same bytes.</summary>
internal sealed class FakePdfRenderer : IVersionedPdfRenderer
{
    public string Version { get; set; } = "chrome-1";

    public Exception? Throw { get; set; }

    public Func<string, string>? Body { get; set; }

    public List<string> Htmls { get; } = [];

    public Task<byte[]> RenderHtmlAsync(string html, PdfRenderOptions? options = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<PdfRenderResult> RenderHtmlWithVersionAsync(string html, PdfRenderOptions? options = null, CancellationToken ct = default)
    {
        Htmls.Add(html);
        if (Throw is not null)
            throw Throw;

        var body = Body?.Invoke(html) ?? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html)));
        var pdf = Encoding.Latin1.GetBytes($"%PDF-1.4\n<< /CreationDate (D:20260101120000+00'00') >>\n{body}\n%%EOF");
        return Task.FromResult(new PdfRenderResult(pdf, Version));
    }
}

internal sealed class FakeNumberSource : ITaxDocumentNumberSource
{
    /// <summary>Numbers allocated and not rolled back.</summary>
    public int Allocated { get; private set; }

    public Task<string> NextNumberAsync(System.Data.Common.DbTransaction tx, string sellerId, TaxDocumentKind kind, DateOnly issueDate, CancellationToken ct = default)
    {
        Allocated++;
        ((InMemoryTaxDocumentStore.FakeTransaction)tx).OnRollback(() => Allocated--);
        return Task.FromResult($"TAX-{Allocated:0000}");
    }
}

internal sealed class ScriptedMailer : ITaxDocumentMailer
{
    private readonly Queue<Func<CancellationToken, Task<TaxDocumentSendResult>>> _script = new();

    public List<TaxDocumentEmail> Sent { get; } = [];

    /// <summary>What to answer when the script is empty.</summary>
    public Func<TaxDocumentSendResult> Default { get; set; } = () => TaxDocumentSendResult.Sent;

    public void Then(Func<TaxDocumentSendResult> next) => _script.Enqueue(_ => Task.FromResult(next()));

    public void ThenAsync(Func<CancellationToken, Task<TaxDocumentSendResult>> next) => _script.Enqueue(next);

    public Task<TaxDocumentSendResult> SendAsync(TaxDocumentEmail email, CancellationToken ct = default)
    {
        Sent.Add(email);
        return _script.Count > 0 ? _script.Dequeue()(ct) : Task.FromResult(Default());
    }
}

internal sealed class RecordingFailureHandler : ITaxDocumentFailureHandler
{
    public List<TaxDocumentFailure> Failures { get; } = [];

    public Exception? Throw { get; set; }

    public Task OnFailedAsync(TaxDocumentFailure failure, CancellationToken ct = default)
    {
        Failures.Add(failure);
        return Throw is null ? Task.CompletedTask : throw Throw;
    }
}

/// <summary>Lets a test act between two steps of the sender, e.g. cancel a document after its ORIGINAL was recorded.</summary>
internal sealed class HookedStore(ITaxDocumentStore inner) : ITaxDocumentStore
{
    public Func<Task>? BeforeBeginSend { get; set; }

    public Func<Task>? BeforeRecordOriginal { get; set; }

    public bool LoseNextMarkSent { get; set; }

    public Task<TaxDocumentRecord?> FindByKeyForIssueAsync(System.Data.Common.DbTransaction tx, TaxDocumentKey key, CancellationToken ct = default) =>
        inner.FindByKeyForIssueAsync(tx, key, ct);

    public Task<TaxDocumentRecord?> GetAsync(System.Data.Common.DbTransaction? tx, Guid id, string sellerId, CancellationToken ct = default) => inner.GetAsync(tx, id, sellerId, ct);

    public Task InsertAsync(System.Data.Common.DbTransaction tx, NewTaxDocument document, CancellationToken ct = default) => inner.InsertAsync(tx, document, ct);

    public Task<TaxDocumentCancelOutcome> CancelForReplacementAsync(
        System.Data.Common.DbTransaction tx, Guid documentId, Guid replacedByDocumentId, string sellerId, DateTimeOffset now, CancellationToken ct = default) =>
        inner.CancelForReplacementAsync(tx, documentId, replacedByDocumentId, sellerId, now, ct);

    public Task<IReadOnlyList<TaxDocumentRecord>> ClaimDueAsync(int max, DateTimeOffset now, TimeSpan lease, Guid claimToken, CancellationToken ct = default) =>
        inner.ClaimDueAsync(max, now, lease, claimToken, ct);

    public async Task<bool> RecordOriginalAsync(Guid id, Guid claimToken, DateTimeOffset now, string pdfSha256, string rendererVersion, CancellationToken ct = default)
    {
        if (BeforeRecordOriginal is { } hook)
            await hook();
        return await inner.RecordOriginalAsync(id, claimToken, now, pdfSha256, rendererVersion, ct);
    }

    public Task<IReadOnlyList<TaxDocumentRenderEvidence>> GetEvidenceAsync(Guid id, CancellationToken ct = default) => inner.GetEvidenceAsync(id, ct);

    public async Task<bool> BeginSendAsync(Guid id, Guid claimToken, DateTimeOffset now, TimeSpan lease, CancellationToken ct = default)
    {
        if (BeforeBeginSend is { } hook)
            await hook();
        return await inner.BeginSendAsync(id, claimToken, now, lease, ct);
    }

    public Task<bool> MarkSentAsync(Guid id, Guid claimToken, DateTimeOffset now, string messageId, CancellationToken ct = default)
    {
        if (LoseNextMarkSent)
        {
            LoseNextMarkSent = false;
            return Task.FromResult(false);
        }

        return inner.MarkSentAsync(id, claimToken, now, messageId, ct);
    }

    public Task<bool> ReleaseAsync(
        Guid id, Guid claimToken, int attempts, DateTimeOffset nextAttemptAt, string errorCode,
        TaxDocumentLeaseHandling leaseHandling = TaxDocumentLeaseHandling.Release, CancellationToken ct = default) =>
        inner.ReleaseAsync(id, claimToken, attempts, nextAttemptAt, errorCode, leaseHandling, ct);

    public Task<bool> FailAsync(
        Guid id, Guid claimToken, int attempts, string errorCode,
        TaxDocumentLeaseHandling leaseHandling = TaxDocumentLeaseHandling.Release, CancellationToken ct = default) =>
        inner.FailAsync(id, claimToken, attempts, errorCode, leaseHandling, ct);

    public Task<bool> RequeueAsync(Guid id, string sellerId, string requestedBy, DateTimeOffset now, CancellationToken ct = default) =>
        inner.RequeueAsync(id, sellerId, requestedBy, now, ct);

    public Task<bool> UpdateBuyerEmailAsync(Guid id, string sellerId, string buyerEmail, CancellationToken ct = default) =>
        inner.UpdateBuyerEmailAsync(id, sellerId, buyerEmail, ct);
}

/// <summary>Everything wired together over the in-memory store.</summary>
internal sealed class Flow
{
    public static readonly DateTimeOffset Start = new(2026, 10, 14, 5, 0, 0, TimeSpan.Zero);
    public const string Seller = "seller-1";

    public Flow(TaxDocumentSendOptions? options = null)
    {
        Options = options ?? new TaxDocumentSendOptions { RevenueDepartmentAddress = "rd@example.test" };
        Clock = new ManualTimeProvider(Start);
        InMemory = new InMemoryTaxDocumentStore();
        Store = new HookedStore(InMemory);
        Renderer = new TaxDocumentRenderer(Pdf);
        Service = new TaxDocumentService(Store, Numbers, Renderer, new TaxDocumentValidationOptions(), Clock, new CapturingLogger<TaxDocumentService>(Logs));
        Sender = new TaxDocumentSender(Store, Mailer, Failures, Renderer, Options, Clock, new CapturingLogger<TaxDocumentSender>(Logs));
    }

    public TaxDocumentSendOptions Options { get; }
    public ManualTimeProvider Clock { get; }
    public InMemoryTaxDocumentStore InMemory { get; }
    public HookedStore Store { get; }
    public FakePdfRenderer Pdf { get; } = new();
    public FakeNumberSource Numbers { get; } = new();
    public ScriptedMailer Mailer { get; } = new();
    public RecordingFailureHandler Failures { get; } = new();
    public LogSink Logs { get; } = new();
    public TaxDocumentRenderer Renderer { get; }
    public TaxDocumentService Service { get; }
    public TaxDocumentSender Sender { get; }

    public static IssueTaxDocumentRequest Request(string source = "pay_1", TaxDocumentSnapshot? snapshot = null, string seller = Seller, string? buyerEmail = "buyer@example.test") =>
        new()
        {
            Key = new TaxDocumentKey((snapshot ?? Samples.Receipt()).Kind, source),
            SellerId = seller,
            Snapshot = snapshot ?? Samples.Receipt(),
            BuyerEmail = buyerEmail,
        };

    /// <summary>Issues in its own transaction, committing only when the outcome says to.</summary>
    public async Task<TaxDocumentIssueResult> IssueAsync(IssueTaxDocumentRequest request)
    {
        await using var tx = new InMemoryTaxDocumentStore.FakeTransaction();
        var result = await Service.IssueAsync(tx, request);
        if (result.Outcome is TaxDocumentIssueOutcome.Issued or TaxDocumentIssueOutcome.AlreadyIssued)
            tx.Commit();
        else
            tx.Rollback();
        return result;
    }

    public async Task<TaxDocumentRecord> IssuedAsync(string source = "pay_1", TaxDocumentSnapshot? snapshot = null, string? buyerEmail = "buyer@example.test")
    {
        var result = await IssueAsync(Request(source, snapshot, buyerEmail: buyerEmail));
        Assert.Equal(TaxDocumentIssueOutcome.Issued, result.Outcome);
        return (await Store.GetAsync(null, result.DocumentId!.Value, Seller))!;
    }

    public async Task<TaxDocumentRecord> GetAsync(Guid id) => (await Store.GetAsync(null, id, Seller))!;
}
