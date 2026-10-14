namespace Themia.ETax.Tests;

/// <summary>
/// The decimal-baht algorithm VatMath was promoted from (opsezy <c>VatMath</c>), kept as the oracle for the
/// sweep and for checking the parity-vector file.
/// </summary>
internal static class OpsezyDecimalReference
{
    public static decimal AddVat(decimal amountExVat, decimal ratePercent) =>
        decimal.Round(amountExVat * (100m + ratePercent) / 100m, 2, MidpointRounding.AwayFromZero);

    public static (decimal ExVat, decimal Vat) Split(decimal amountInclVat, decimal ratePercent)
    {
        var exVat = decimal.Round(amountInclVat * 100m / (100m + ratePercent), 2, MidpointRounding.AwayFromZero);
        return (exVat, amountInclVat - exVat);
    }

    /// <summary>True when the unrounded baht value sits exactly halfway between two satang.</summary>
    public static bool IsMidpoint(decimal unroundedBaht) => unroundedBaht * 100m % 1m == 0.5m;
}
