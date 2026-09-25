namespace Themia.Payments;

/// <summary>An amount in a currency's smallest unit.</summary>
/// <remarks>
/// Minor units, not <see cref="decimal"/>: every provider in this space is integer-minor-unit on the wire
/// (Beam takes <c>10000</c> for 100.00 THB), and a decimal puts both the rounding and the currency in the
/// caller's head at each call site. An adapter whose provider wants a decimal formats it at its own edge.
/// </remarks>
public readonly record struct Money
{
    private Money(long minorUnits, string currency)
    {
        MinorUnits = minorUnits;
        Currency = currency;
    }

    /// <summary>The amount, in the currency's smallest unit. Never negative.</summary>
    public long MinorUnits { get; }

    /// <summary>The ISO 4217 code, upper-cased.</summary>
    public string Currency { get; }

    /// <summary>An amount in Thai baht.</summary>
    /// <param name="minorUnits">Satang. <c>10000</c> is 100.00 THB.</param>
    public static Money Thb(long minorUnits) => From(minorUnits, "THB");

    /// <summary>An amount in any currency.</summary>
    /// <param name="minorUnits">The amount in the currency's smallest unit.</param>
    /// <param name="currency">An ISO 4217 code; case is normalised.</param>
    /// <exception cref="ArgumentException"><paramref name="currency"/> is not three ASCII letters.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minorUnits"/> is negative.</exception>
    public static Money From(long minorUnits, string currency)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minorUnits);
        ArgumentNullException.ThrowIfNull(currency);
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetter))
        {
            throw new ArgumentException($"'{currency}' is not an ISO 4217 code.", nameof(currency));
        }

        return new Money(minorUnits, currency.ToUpperInvariant());
    }
}
