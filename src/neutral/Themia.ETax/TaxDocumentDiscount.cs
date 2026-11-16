namespace Themia.ETax;

/// <summary>
/// A discount taken off the purchase on a tax invoice/receipt (credit spent against the order, a coupon). The lines keep the full purchase amount ex-VAT; the
/// document prints purchase, less the discount, the remaining amount, VAT on the remaining amount, and the total.
/// <see cref="TaxDocumentTotals.ExVatSatang"/> is the remaining amount: the sum of the lines minus <see cref="AmountExVatSatang"/>.
/// </summary>
/// <remarks>
/// Themia does not derive the amounts. To keep the printed total equal to the cash actually paid, split the paid amount and take the discount as the difference:
/// <c>net = VatMath.Split(cashPaid, rate).ExVatSatang</c>, <c>discount = VatMath.Split(orderTotal, rate).ExVatSatang - net</c>. Splitting the credit on its own
/// can land a satang off the cash paid.
/// </remarks>
public sealed record TaxDocumentDiscount
{
    /// <summary>The label printed on the "less" row (for example, "ส่วนลดจากเครดิต").</summary>
    public required string Description { get; init; }

    /// <summary>The discount before VAT, in satang. Zero or more, and at most the sum of the lines; equal to the sum means a remaining amount, VAT and total of zero.</summary>
    public required long AmountExVatSatang { get; init; }
}
