namespace Themia.Payments;

/// <summary>Which methods an amount may be paid with.</summary>
/// <remarks>
/// The fee on a payment depends on how it is paid: a QR transfer is a flat fee, a card is a percentage. On a
/// small charge a card fee can be a tenth of the whole amount. This is the rule an adopter writes to say so —
/// it does not pick the cheapest method, reorder anything, or model fees, because rates are per-merchant
/// contract terms that change without a release.
/// </remarks>
public sealed class PaymentMethodPolicy
{
    /// <summary>The ISO 4217 code these bands are written in. Stored upper-case, as <see cref="Money"/> stores it,
    /// so a configured <c>"thb"</c> matches <see cref="Money.Thb"/>.</summary>
    public string Currency
    {
        get => currency;
        init => currency = value?.ToUpperInvariant() ?? "";
    }

    private readonly string currency = "THB";

    /// <summary>Bands in ascending order of their upper bound.</summary>
    public IReadOnlyList<PaymentMethodBand> Bands { get; init; } = [];

    /// <summary>The methods allowed above the last band.</summary>
    public IReadOnlyList<PaymentMethod> Above { get; init; } = [];

    /// <summary>The methods this policy allows for an amount.</summary>
    /// <param name="amount">The charge amount.</param>
    /// <returns>The allowed methods.</returns>
    /// <exception cref="PaymentApiException">The amount is in another currency.</exception>
    public IReadOnlyList<PaymentMethod> Resolve(Money amount)
    {
        if (!string.Equals(amount.Currency, Currency, StringComparison.Ordinal))
        {
            throw new PaymentApiException(
                FailureKind.Validation, "policy_currency_mismatch", httpStatus: 0,
                $"The method policy is written in {Currency}; this charge is in {amount.Currency}.");
        }

        foreach (var band in Bands)
        {
            if (amount.MinorUnits <= band.UpToMinorUnitsInclusive)
            {
                return band.Methods;
            }
        }

        return Above;
    }

    /// <summary>Configuration errors in this policy, empty when it is usable.</summary>
    /// <returns>One message per problem.</returns>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (Currency.Length != 3 || !Currency.All(char.IsAsciiLetter))
        {
            errors.Add($"MethodPolicy.Currency '{Currency}' is not an ISO 4217 code.");
        }

        for (var i = 0; i < Bands.Count; i++)
        {
            if (Bands[i].Methods.Count == 0)
            {
                errors.Add($"MethodPolicy.Bands[{i}] allows no method, so a charge in that band could never be paid.");
            }

            if (i > 0 && Bands[i].UpToMinorUnitsInclusive <= Bands[i - 1].UpToMinorUnitsInclusive)
            {
                errors.Add("MethodPolicy.Bands must be in ascending order of UpToMinorUnitsInclusive, with no duplicates.");
            }
        }

        if (Above.Count == 0)
        {
            errors.Add("MethodPolicy.Above allows no method, so any amount above the last band could never be paid.");
        }

        return errors;
    }

    /// <summary>Every method this policy can ever produce.</summary>
    /// <returns>The distinct methods named anywhere in it.</returns>
    public IReadOnlyList<PaymentMethod> AllNamedMethods() =>
        Bands.SelectMany(b => b.Methods).Concat(Above).Distinct().ToArray();
}
