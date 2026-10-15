namespace Themia.ETax.Withholding;

/// <summary>
/// Withholding arithmetic in whole satang with rates in basis points (300 = 3%). Rounds UP to the satang, looking at every
/// digit after the second decimal of baht (ceiling): 10.115, 10.114 and 10.1101 all become 10.12, and an exact 10.11 stays 10.11.
/// </summary>
/// <remarks>
/// The Revenue Department's rule for withholding tax (coord #0164 [9]). It is deliberately NOT the rule of <see cref="VatMath"/>, which rounds
/// half-up. Which base the rate applies to (VAT-exclusive or not) and which rate applies are the caller's business rules; this type only multiplies.
/// </remarks>
public static class WithholdingMath
{
    /// <summary>The highest accepted rate: 100%.</summary>
    public const int MaxRateBasisPoints = VatMath.MaxRateBasisPoints;

    private const long BasisPointScale = 10_000;

    /// <summary>The tax to withhold from <paramref name="baseSatang"/>: <c>ceiling(base × rate / 10000)</c>.</summary>
    /// <param name="baseSatang">The amount the rate applies to, in satang.</param>
    /// <param name="rateBasisPoints">The rate in basis points, 0..10000.</param>
    /// <exception cref="ArgumentOutOfRangeException">The base is negative or the rate is outside 0..10000.</exception>
    public static long Withhold(long baseSatang, int rateBasisPoints)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(baseSatang);
        ArgumentOutOfRangeException.ThrowIfNegative(rateBasisPoints);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rateBasisPoints, MaxRateBasisPoints);

        // Both operands are non-negative, so adding scale - 1 before the integer division is the ceiling. The rate is at most 100%, so the
        // result never exceeds the base and always fits a long.
        var numerator = (Int128)baseSatang * rateBasisPoints;
        return (long)((numerator + BasisPointScale - 1) / BasisPointScale);
    }
}
