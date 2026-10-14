using System.Text.Json.Serialization;

namespace Themia.ETax;

/// <summary>The kind of tax document. Persisted by name, so renaming a member is a breaking change.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TaxDocumentKind>))]
public enum TaxDocumentKind
{
    /// <summary>Tax invoice / receipt (ใบเสร็จรับเงิน/ใบกำกับภาษี, s.86/4).</summary>
    TaxInvoiceReceipt = 1,

    /// <summary>Credit note (ใบลดหนี้, s.86/9).</summary>
    CreditNote = 2,

    /// <summary>Debit note (ใบเพิ่มหนี้, s.86/10).</summary>
    DebitNote = 3,
}
