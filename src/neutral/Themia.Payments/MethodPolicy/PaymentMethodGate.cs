using Microsoft.Extensions.Options;

namespace Themia.Payments;

/// <summary>Applies the configured <see cref="PaymentMethodPolicy"/> to a caller's chosen methods.</summary>
/// <remarks>
/// Adapters call this once, before building a payload. It intersects rather than replaces: a caller that
/// asked for one method usually has a reason (a saved card, a retry of a failed attempt), so an empty
/// intersection is an error naming both lists rather than a silent substitution.
/// </remarks>
public sealed class PaymentMethodGate
{
    private readonly IOptions<ThemiaPaymentsOptions> options;

    /// <summary>Creates the gate.</summary>
    /// <param name="options">The shared options.</param>
    public PaymentMethodGate(IOptions<ThemiaPaymentsOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    /// <summary>The methods a charge may actually offer.</summary>
    /// <param name="amount">The charge amount.</param>
    /// <param name="requested">What the caller asked for.</param>
    /// <returns>The caller's list, filtered by the policy and in the caller's order.</returns>
    /// <exception cref="PaymentApiException">The policy leaves nothing the caller asked for.</exception>
    public IReadOnlyList<PaymentMethod> Apply(Money amount, IReadOnlyList<PaymentMethod> requested)
    {
        ArgumentNullException.ThrowIfNull(requested);

        var policy = options.Value.MethodPolicy;
        if (policy is null)
        {
            return requested;
        }

        var allowed = policy.Resolve(amount);
        var kept = requested.Where(allowed.Contains).ToArray();
        if (kept.Length > 0)
        {
            return kept;
        }

        throw new PaymentApiException(
            FailureKind.Validation, "method_not_allowed_for_amount", httpStatus: 0,
            $"The caller asked for [{string.Join(", ", requested)}] but the policy allows " +
            $"[{string.Join(", ", allowed)}] at {amount.MinorUnits} {amount.Currency}.");
    }
}
