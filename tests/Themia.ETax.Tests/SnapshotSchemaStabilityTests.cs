using Xunit;

namespace Themia.ETax.Tests;

/// <summary>
/// A snapshot issued before a field existed must serialise to the same bytes and hash for ever: stored certificates are verified against
/// the hash of their stored JSON, and a new optional field is only safe because it is omitted from the JSON when it is null. These JSON
/// strings and hashes were captured from the serializer BEFORE the A5 fields (line quantity/unit price, per-line original and correct
/// value, payment, note reason) were added. A failure means a field leaked into old snapshots; it never means update the literal.
/// </summary>
public sealed class SnapshotSchemaStabilityTests
{
    private const string ReceiptJson = """{"schema_version":1,"kind":"TaxInvoiceReceipt","document_number":"TAX-2026-000001","issue_date":"2026-10-14","seller":{"name":"\u0E1A\u0E23\u0E34\u0E29\u0E31\u0E17 \u0E15\u0E31\u0E27\u0E2D\u0E22\u0E48\u0E32\u0E07 \u0E08\u0E33\u0E01\u0E31\u0E14","tax_id":"0105536078673","branch":"00000","address":"1 \u0E16\u0E19\u0E19\u0E2A\u0E38\u0E02\u0E38\u0E21\u0E27\u0E34\u0E17 \u0E01\u0E23\u0E38\u0E07\u0E40\u0E17\u0E1E\u0E21\u0E2B\u0E32\u0E19\u0E04\u0E23"},"buyer":{"name":"\u0E04\u0E38\u0E13\u0E2A\u0E21\u0E0A\u0E32\u0E22 \u0E43\u0E08\u0E14\u0E35","tax_id":"1101700230708"},"lines":[{"description":"Boost package","amount_ex_vat_satang":100000}],"totals":{"ex_vat_satang":100000,"vat_satang":7000,"incl_vat_satang":107000,"withheld_satang":3000,"net_received_satang":104000,"vat_rate_basis_points":700}}""";
    private const string ReceiptHash = "819b06dc3d2cb9f5e9b7cc63816a2da3b67d4cccce678e1d574ab61ce2372809";
    private const string FullCancellationJson = """{"schema_version":1,"kind":"CreditNote","document_number":"CN-2026-000001","issue_date":"2026-10-14","seller":{"name":"\u0E1A\u0E23\u0E34\u0E29\u0E31\u0E17 \u0E15\u0E31\u0E27\u0E2D\u0E22\u0E48\u0E32\u0E07 \u0E08\u0E33\u0E01\u0E31\u0E14","tax_id":"0105536078673","branch":"00000","address":"1 \u0E16\u0E19\u0E19\u0E2A\u0E38\u0E02\u0E38\u0E21\u0E27\u0E34\u0E17 \u0E01\u0E23\u0E38\u0E07\u0E40\u0E17\u0E1E\u0E21\u0E2B\u0E32\u0E19\u0E04\u0E23"},"buyer":{"name":"\u0E04\u0E38\u0E13\u0E2A\u0E21\u0E0A\u0E32\u0E22 \u0E43\u0E08\u0E14\u0E35","tax_id":"1101700230708"},"lines":[{"description":"Boost package","amount_ex_vat_satang":100000}],"totals":{"ex_vat_satang":100000,"vat_satang":7000,"incl_vat_satang":107000,"withheld_satang":3000,"net_received_satang":104000,"vat_rate_basis_points":700},"note":{"original_document_id":"11111111-1111-1111-1111-111111111111","original_document_number":"TAX-2026-000001","original_issue_date":"2026-10-14","mode":"FullCancellation","reason":"Refund","refunded_net_satang":104000}}""";
    private const string FullCancellationHash = "ff72a74f67d479dddc525271688bae5e9a385aed033ded8555b9e84f82189193";
    private const string AdjustmentJson = """{"schema_version":1,"kind":"CreditNote","document_number":"NOTE-2026-000001","issue_date":"2026-10-14","seller":{"name":"\u0E1A\u0E23\u0E34\u0E29\u0E31\u0E17 \u0E15\u0E31\u0E27\u0E2D\u0E22\u0E48\u0E32\u0E07 \u0E08\u0E33\u0E01\u0E31\u0E14","tax_id":"0105536078673","branch":"00000","address":"1 \u0E16\u0E19\u0E19\u0E2A\u0E38\u0E02\u0E38\u0E21\u0E27\u0E34\u0E17 \u0E01\u0E23\u0E38\u0E07\u0E40\u0E17\u0E1E\u0E21\u0E2B\u0E32\u0E19\u0E04\u0E23"},"buyer":{"name":"\u0E04\u0E38\u0E13\u0E2A\u0E21\u0E0A\u0E32\u0E22 \u0E43\u0E08\u0E14\u0E35","tax_id":"1101700230708"},"lines":[{"description":"Adjustment","amount_ex_vat_satang":50000}],"totals":{"ex_vat_satang":50000,"vat_satang":3500,"incl_vat_satang":53500,"withheld_satang":0,"net_received_satang":53500,"vat_rate_basis_points":700},"note":{"original_document_id":"11111111-1111-1111-1111-111111111111","original_document_number":"TAX-2026-000001","original_issue_date":"2026-10-14","mode":"Adjustment","reason":"Price correction","original_value_satang":107000,"corrected_value_satang":53500}}""";
    private const string AdjustmentHash = "327ae941853fde9f6ec555ff4b18f607580c1de02043e5c52bf516915bd60315";

    public static TheoryData<string, string, string> Pinned => new()
    {
        { "receipt", ReceiptJson, ReceiptHash },
        { "full-cancellation", FullCancellationJson, FullCancellationHash },
        { "adjustment", AdjustmentJson, AdjustmentHash },
    };

    // The samples now carry a reason kind; an old snapshot never had one.
    private static TaxDocumentSnapshot Sample(string name) => name switch
    {
        "receipt" => Samples.Receipt(),
        "full-cancellation" => WithoutReasonKind(Samples.FullCancellationCreditNote()),
        "adjustment" => WithoutReasonKind(Samples.AdjustmentNote(TaxDocumentKind.CreditNote, 107_000, 53_500)),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static TaxDocumentSnapshot WithoutReasonKind(TaxDocumentSnapshot s) => s with { Note = s.Note! with { ReasonKind = null } };

    [Theory]
    [MemberData(nameof(Pinned))]
    public void Serialize_ShouldReproduceTheStoredJsonAndHashOfAnOldSnapshot(string name, string json, string hash)
    {
        var serialised = TaxDocumentSnapshotSerializer.Serialize(Sample(name));

        Assert.Equal(json, serialised);
        Assert.Equal(hash, TaxDocumentSnapshotSerializer.ComputeHash(serialised));
    }

    [Theory]
    [MemberData(nameof(Pinned))]
    public void Deserialize_ShouldReadAnOldSnapshotAndWriteItBackByteIdentical(string name, string json, string hash)
    {
        var back = TaxDocumentSnapshotSerializer.Deserialize(json, hash);

        Assert.Equal(json, TaxDocumentSnapshotSerializer.Serialize(back));
        _ = name;
    }
}
