namespace Themia.ETax;

/// <summary>How and when a receipt was paid, as printed in the payment line. Receipts only.</summary>
public sealed record TaxPayment
{
    /// <summary>How it was paid.</summary>
    public required TaxPaymentMethod Method { get; init; }

    /// <summary>The day the money arrived; left off the printed line when <see langword="null"/>.</summary>
    public DateOnly? Date { get; init; }
}
