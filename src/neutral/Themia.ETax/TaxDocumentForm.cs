using System.Text.Json.Serialization;

namespace Themia.ETax;

/// <summary>
/// How much a <see cref="TaxDocumentKind.TaxInvoiceReceipt"/> prints. Persisted by name; <see cref="Full"/> is the default and is
/// omitted from the stored JSON, so a snapshot issued before forms existed keeps its hash.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TaxDocumentForm>))]
public enum TaxDocumentForm
{
    /// <summary>The full tax invoice: buyer name, address and tax ID printed.</summary>
    Full = 0,

    /// <summary>The abbreviated tax invoice (ใบกำกับภาษีอย่างย่อ): no buyer block, the price stated as VAT included.</summary>
    Abbreviated = 1,
}
