namespace Themia.ETax;

/// <summary>
/// VAT arithmetic in whole satang with rates in basis points (700 = 7%). Rounds midpoints away from zero and
/// reproduces, for every input, the decimal-baht results of the algorithm it was promoted from, so a tax
/// document never disagrees with the payment by a satang.
/// </summary>
public static class VatMath
{
    /// <summary>The highest accepted rate: 100%.</summary>
    public const int MaxRateBasisPoints = 10_000;

    private const long BasisPointScale = 10_000;

    /// <summary>Adds VAT to a VAT-exclusive amount: <c>incl = round(ex × (10000 + rate) / 10000)</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The amount is negative or the rate is outside 0..10000.</exception>
    /// <exception cref="OverflowException">The result does not fit in a <see cref="long"/>.</exception>
    public static VatAmounts AddVat(long exVatSatang, int rateBasisPoints)
    {
        Validate(exVatSatang, rateBasisPoints);

        var incl = DivideRoundingHalfUp((Int128)exVatSatang * (BasisPointScale + rateBasisPoints), BasisPointScale);
        return new VatAmounts(exVatSatang, checked((long)incl) - exVatSatang);
    }

    /// <summary>
    /// Separates a VAT-inclusive amount: <c>ex = round(incl × 10000 / (10000 + rate))</c>, and the VAT is the
    /// remainder so the two add back to the amount exactly.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The amount is negative or the rate is outside 0..10000.</exception>
    public static VatAmounts Split(long inclVatSatang, int rateBasisPoints)
    {
        Validate(inclVatSatang, rateBasisPoints);

        var ex = (long)DivideRoundingHalfUp((Int128)inclVatSatang * BasisPointScale, BasisPointScale + rateBasisPoints);
        return new VatAmounts(ex, inclVatSatang - ex);
    }

    // Both operands are non-negative here, so half-up equals half-away-from-zero.
    private static Int128 DivideRoundingHalfUp(Int128 numerator, long denominator) =>
        (numerator * 2 + denominator) / ((Int128)denominator * 2);

    private static void Validate(long amountSatang, int rateBasisPoints)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amountSatang);
        ArgumentOutOfRangeException.ThrowIfNegative(rateBasisPoints);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rateBasisPoints, MaxRateBasisPoints);
    }
}
