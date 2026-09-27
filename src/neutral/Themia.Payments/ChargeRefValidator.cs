namespace Themia.Payments;

/// <summary>Rejects a <see cref="ChargeRef"/> that names no charge at all, the same way in every adapter.</summary>
public static class ChargeRefValidator
{
    /// <summary>Validates a charge reference.</summary>
    /// <param name="charge">The reference, as the caller passed it.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="charge"/> has neither a <see cref="ChargeRef.ProviderChargeId"/> nor a
    /// <see cref="ChargeRef.ReferenceId"/> (null, empty and whitespace all count as unset).
    /// </exception>
    public static void Validate(ChargeRef charge)
    {
        if (string.IsNullOrWhiteSpace(charge.ProviderChargeId) && string.IsNullOrWhiteSpace(charge.ReferenceId))
        {
            throw new ArgumentException("A ChargeRef needs a ReferenceId or a ProviderChargeId.", nameof(charge));
        }
    }
}
