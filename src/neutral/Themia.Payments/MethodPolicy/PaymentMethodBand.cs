namespace Themia.Payments;

/// <summary>One amount band and the methods allowed inside it.</summary>
/// <param name="UpToMinorUnitsInclusive">The band's upper bound, <b>in minor units and inclusive</b>:
/// <c>100000</c> is 1,000.00 THB, and an amount of exactly that is inside this band.</param>
/// <param name="Methods">The methods allowed at or below that amount.</param>
public sealed record PaymentMethodBand(long UpToMinorUnitsInclusive, IReadOnlyList<PaymentMethod> Methods);
