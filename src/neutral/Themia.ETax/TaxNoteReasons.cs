namespace Themia.ETax;

/// <summary>The stable code of each <see cref="TaxNoteReason"/>, the value <see cref="TaxNoteReference.ReasonCode"/> must carry when both are set.</summary>
public static class TaxNoteReasons
{
    /// <summary>The code of <paramref name="reason"/>.</summary>
    /// <param name="reason">The reason.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason"/> is not a defined value.</exception>
    public static string CodeOf(TaxNoteReason reason) => reason switch
    {
        TaxNoteReason.PriceReduction => "price_reduction",
        TaxNoteReason.GoodsReturned => "goods_returned",
        TaxNoteReason.Compensation => "compensation",
        TaxNoteReason.DepositRefund => "deposit_refund",
        TaxNoteReason.ServiceCancelled => "service_cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown credit note reason."),
    };
}
