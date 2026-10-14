namespace Themia.ETax;

/// <summary>A VAT-exclusive amount and its VAT, in satang. The two always add back to <see cref="InclVatSatang"/> exactly.</summary>
/// <param name="ExVatSatang">The amount before VAT.</param>
/// <param name="VatSatang">The VAT.</param>
public readonly record struct VatAmounts(long ExVatSatang, long VatSatang)
{
    /// <summary>The VAT-inclusive amount.</summary>
    public long InclVatSatang => checked(ExVatSatang + VatSatang);
}
