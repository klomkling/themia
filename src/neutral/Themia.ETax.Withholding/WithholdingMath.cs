namespace Themia.ETax.Withholding;

/// <summary>
/// Withholding arithmetic in whole satang with rates in basis points (300 = 3%). Rounds midpoints up (half-up), the same
/// rule as <see cref="VatMath"/>.
/// </summary>
/// <remarks>
/// The rounding rule is PENDING confirmation by an accountant (coord #0164 Q8). Which base the rate applies to
/// (VAT-exclusive or not) and which rate applies are the caller's business rules; this type only multiplies.
/// </remarks>
public static class WithholdingMath
{
    /// <summary>The highest accepted rate: 100%.</summary>
    public const int MaxRateBasisPoints = VatMath.MaxRateBasisPoints;

    private const long BasisPointScale = 10_000;

    /// <summary>The tax to withhold from <paramref name="baseSatang"/>: <c>round(base × rate / 10000)</c>.</summary>
    /// <param name="baseSatang">The amount the rate applies to, in satang.</param>
    /// <param name="rateBasisPoints">The rate in basis points, 0..10000.</param>
    /// <exception cref="ArgumentOutOfRangeException">The base is negative or the rate is outside 0..10000.</exception>
    public static long Withhold(long baseSatang, int rateBasisPoints)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(baseSatang);
        ArgumentOutOfRangeException.ThrowIfNegative(rateBasisPoints);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rateBasisPoints, MaxRateBasisPoints);

        // Both operands are non-negative, so half-up equals half-away-from-zero. The result never exceeds the base.
        var numerator = (Int128)baseSatang * rateBasisPoints;
        return (long)((numerator * 2 + BasisPointScale) / (BasisPointScale * 2));
    }
}
