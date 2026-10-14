using Xunit;

namespace Themia.ETax.Tests;

/// <summary>
/// A snapshot holds names, tax ids and addresses, and a mail transport's exception text can echo the recipient.
/// None of it may reach a log line, a structured log value, or an exception message this package produces.
/// </summary>
public sealed class NoPiiInLogsTests
{
    private const string Name = "PII-NAME-9F3";
    private const string Address = "PII-ADDR-9F3 Road";
    private const string Line = "PII-LINE-9F3";
    private const string Email = "pii-9f3@example.test";
    private static readonly string[] Markers = [Name, Address, Line, Email, Samples.SellerTaxId, Samples.BuyerTaxId, "PII-SELLER-9F3"];

    private static TaxDocumentSnapshot Snapshot() => Samples.Receipt() with
    {
        Seller = Samples.Receipt().Seller with { Name = "PII-SELLER-9F3" },
        Buyer = new TaxParty { Name = Name, TaxId = Samples.BuyerTaxId, Address = Address },
        Lines = [new TaxDocumentLine { Description = Line, AmountExVatSatang = 100_000 }],
    };

    private static void AssertClean(string text, string where)
    {
        foreach (var marker in Markers)
            Assert.False(text.Contains(marker, StringComparison.Ordinal), $"{where} leaked '{marker}':\n{text}");
    }

    [Fact]
    public async Task EveryPathThroughIssueSendFailRequeueAndCopy_ShouldKeepPiiOutOfLogsResultsAndExceptions()
    {
        var flow = new Flow();
        var exceptionTexts = new List<string>();

        // Issue: success, duplicate, and a validation failure.
        var issued = await flow.IssuedAsync("pay_ok", Snapshot(), Email);
        await flow.IssueAsync(Flow.Request("pay_ok", Snapshot(), buyerEmail: Email));
        var invalid = await flow.IssueAsync(Flow.Request("pay_bad", Snapshot() with { Buyer = new TaxParty { Name = Name + "中" } }));
        AssertClean(string.Join(" ", invalid.Errors.Select(e => e.ToString())), "validation errors");

        // Send: the transport throws with the recipient in its message, then answers with codes, then permanently fails.
        flow.Mailer.Then(() => throw new InvalidOperationException($"550 mailbox {Email} for {Name} unavailable"));
        await flow.Sender.RunOnceAsync();
        flow.Clock.Advance(flow.Options.MaxBackoff);
        flow.Mailer.Then(() => TaxDocumentSendResult.Transient("smtp_4xx"));
        await flow.Sender.RunOnceAsync();
        flow.Clock.Advance(flow.Options.MaxBackoff);
        flow.Failures.Throw = new InvalidOperationException($"pager failed for {Email} {Name}");
        flow.Mailer.Then(() => TaxDocumentSendResult.Permanent("smtp_5xx"));
        await flow.Sender.RunOnceAsync();
        AssertClean(string.Join(" ", flow.Failures.Failures.Select(f => f.ToString())), "failure notification");

        // Render failure with PII in the exception message.
        var second = await flow.IssuedAsync("pay_render", Snapshot(), Email);
        flow.Pdf.Throw = new TimeoutException($"timed out rendering {Name} {Address}");
        await flow.Sender.RunOnceAsync();
        flow.Pdf.Throw = null;

        // Requeue, copy, and a tampered snapshot.
        await flow.Service.RequeueAsync(issued.Id, Flow.Seller, "operator-1");
        await flow.Service.RenderCopyAsync(second.Id, Flow.Seller);
        flow.InMemory.Replace(second.Id, r => r with { SnapshotJson = r.SnapshotJson.Replace("TAX-0002", "TAX-0099") });
        var tamper = await Record.ExceptionAsync(() => flow.Service.RenderCopyAsync(second.Id, Flow.Seller));
        exceptionTexts.Add(tamper?.ToString() ?? string.Empty);
        await flow.Sender.RunOnceAsync();

        Assert.NotEmpty(flow.Logs.Entries);
        AssertClean(flow.Logs.AllText, "log output");
        AssertClean(string.Join("\n", exceptionTexts), "exception");
        Assert.NotNull(tamper);
    }

    [Fact]
    public void SnapshotExceptions_ShouldCarryAFailureCodeOnly()
    {
        var json = TaxDocumentSnapshotSerializer.Serialize(Snapshot());

        var ex = Assert.Throws<TaxDocumentSnapshotException>(() => TaxDocumentSnapshotSerializer.Deserialize(json + " ", "bad"));

        AssertClean(ex.ToString(), "TaxDocumentSnapshotException");
    }

    [Fact]
    public async Task RenderMismatchException_ShouldCarryVersionsOnly()
    {
        var ex = Assert.Throws<TaxDocumentRenderMismatchException>(
            () => TaxDocumentEvidence.Check([new("a", "v1")], new TaxDocumentRender([1], "b", "v1")));

        AssertClean(ex.ToString(), "TaxDocumentRenderMismatchException");
        await Task.CompletedTask;
    }
}
