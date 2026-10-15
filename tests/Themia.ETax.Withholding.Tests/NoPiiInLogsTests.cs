using Xunit;

namespace Themia.ETax.Withholding.Tests;

/// <summary>
/// A snapshot holds names, tax ids and addresses. None of it may reach a log line, a structured log value, a validation error or an
/// exception message this package produces.
/// </summary>
public sealed class NoPiiInLogsTests
{
    private const string Name = "PII-NAME-9F3";
    private const string Address = "PII-ADDR-9F3 Road";
    private const string Description = "PII-DESC-9F3";
    private const string PayerName = "PII-PAYER-9F3";
    private static readonly string[] Markers = [Name, Address, Description, PayerName, Samples.ValidPayeeTaxId, Samples.ValidPayerTaxId];

    private static WithholdingCertificateSnapshot Snapshot() => Samples.Valid() with
    {
        Payer = Samples.Valid().Payer with { Name = PayerName },
        Payee = new TaxParty { Name = Name, TaxId = Samples.ValidPayeeTaxId, Address = Address },
        Rows = [Samples.Valid().Rows[0] with { Description = Description }],
    };

    private static void AssertClean(string text, string where)
    {
        foreach (var marker in Markers)
            Assert.False(text.Contains(marker, StringComparison.Ordinal), $"{where} leaked '{marker}':\n{text}");
    }

    [Fact]
    public async Task EveryPathThroughIssueAndReplace_ShouldKeepPiiOutOfLogsResultsAndExceptions()
    {
        var flow = new Flow();
        var exceptionTexts = new List<string>();

        var first = await flow.IssuedAsync("pay_ok", Snapshot());
        await flow.IssueAsync(Flow.Request("pay_ok", Snapshot()));
        await flow.IssueAsync(Flow.Request("pay_ok", Snapshot(), payer: "payer-2"));
        var invalid = await flow.IssueAsync(Flow.Request("pay_bad", Snapshot() with { Payee = new TaxParty { Name = Name + "中", TaxId = "123", Address = Address } }));
        AssertClean(string.Join(" ", invalid.Errors.Select(e => e.ToString())), "validation errors");
        var broken = await flow.IssueAsync(Flow.Request("pay_broken", Snapshot() with { Payer = null! }));
        AssertClean(string.Join(" ", broken.Errors.Select(e => e.ToString())), "malformed errors");
        var replaced = await flow.IssueAsync(Flow.Request(
            "pay_ok",
            Snapshot() with
            {
                Replacement = new TaxDocumentReplacement
                {
                    ReplacesDocumentId = first,
                    OriginalDocumentNumber = "WHT-2569-000001",
                    OriginalIssueDate = new DateOnly(2026, 10, 15),
                    Reason = "wrong",
                },
            },
            revision: 2));
        Assert.Equal(WithholdingIssueOutcome.Issued, replaced.Outcome);

        // A tampered snapshot is refused with a message that names no content.
        var row = flow.Row(first);
        var tamper = Record.Exception(() => WithholdingSnapshotSerializer.Deserialize(row.SnapshotJson.Replace("WHT-2569-000001", "WHT-2569-000099"), row.SnapshotSha256));
        exceptionTexts.Add(tamper?.ToString() ?? string.Empty);
        var malformed = Record.Exception(() => WithholdingSnapshotSerializer.Deserialize(
            row.SnapshotJson.Replace("\"amount_paid_satang\":1000000", "\"amount_paid_satang\":\"x\"", StringComparison.Ordinal),
            WithholdingSnapshotSerializer.ComputeHash(row.SnapshotJson.Replace("\"amount_paid_satang\":1000000", "\"amount_paid_satang\":\"x\"", StringComparison.Ordinal))));
        exceptionTexts.Add(malformed?.ToString() ?? string.Empty);

        Assert.NotEmpty(flow.Logs.Entries);
        AssertClean(flow.Logs.AllText, "log output");
        AssertClean(string.Join("\n", exceptionTexts), "exception");
    }
}
