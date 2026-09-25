using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Themia.Payments;

/// <summary>Fails startup on a policy the registered adapter could never satisfy.</summary>
/// <remarks>
/// An <see cref="IValidateOptions{TOptions}"/> rather than a fluent predicate, because the check needs the
/// adapter's <see cref="IPaymentGatewayCapabilities"/>. A policy that can produce a method the adapter does
/// not support is a configuration error, and boot is the only safe time to find it.
/// <para>
/// Takes <see cref="IServiceProvider"/> and resolves the capabilities at validation time, rather than taking
/// them as a constructor parameter, for two reasons. The capabilities are optional — the core can be
/// registered with no adapter — and an optional constructor dependency cannot be expressed with the
/// type-based <c>ServiceDescriptor</c> that <c>TryAddEnumerable</c> requires. And resolving late means the
/// order of <c>AddThemiaPayments</c> and the adapter's own <c>Add…</c> does not matter.
/// </para>
/// </remarks>
internal sealed class PaymentMethodPolicyValidator : IValidateOptions<ThemiaPaymentsOptions>
{
    private readonly IServiceProvider services;

    public PaymentMethodPolicyValidator(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        this.services = services;
    }

    public ValidateOptionsResult Validate(string? name, ThemiaPaymentsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MethodPolicy is not { } policy)
        {
            return ValidateOptionsResult.Success;
        }

        var errors = policy.Validate().ToList();

        if (services.GetService<IPaymentGatewayCapabilities>() is { } capabilities)
        {
            var unsupported = policy.AllNamedMethods().Except(capabilities.SupportedMethods).ToArray();
            if (unsupported.Length > 0)
            {
                errors.Add(
                    $"MethodPolicy names [{string.Join(", ", unsupported)}], which the registered payment " +
                    $"adapter does not support (it supports [{string.Join(", ", capabilities.SupportedMethods)}]).");
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
