using System.Text.Json.Serialization;

namespace Themia.ETax;

/// <summary>Why a credit note was issued: the cases section 82/10 of the Revenue Code allows.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TaxNoteReason>))]
public enum TaxNoteReason
{
    /// <summary>The price was reduced: a defect, a short delivery or a wrong calculation.</summary>
    PriceReduction = 1,

    /// <summary>Goods were returned.</summary>
    GoodsReturned = 2,

    /// <summary>Compensation was paid.</summary>
    Compensation = 3,

    /// <summary>A deposit or advance payment was refunded.</summary>
    DepositRefund = 4,

    /// <summary>A service contract was cancelled.</summary>
    ServiceCancelled = 5,
}
