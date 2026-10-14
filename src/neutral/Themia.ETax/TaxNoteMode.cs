using System.Text.Json.Serialization;

namespace Themia.ETax;

/// <summary>How a credit or debit note relates to the document it references.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TaxNoteMode>))]
public enum TaxNoteMode
{
    /// <summary>
    /// Cancels the whole referenced document: the note repeats its amounts and shows the cash actually
    /// refunded, which is below the invoice total when the customer withheld tax. Credit notes only.
    /// </summary>
    FullCancellation = 1,

    /// <summary>The note carries the difference between the original and a corrected value.</summary>
    Adjustment = 2,
}
