using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Themia.SecondFactor;

// Fails host start when the admin reset is on but the host supplied no act-on rule or no way to end the target's sessions. There is no
// default that allows, and no silent "nothing to revoke": a host with no sessions registers a no-op observer on purpose.
internal sealed class AdminPolicyValidator(IServiceCollection services) : IValidateOptions<SecondFactorOptions>
{
    public ValidateOptionsResult Validate(string? name, SecondFactorOptions options)
    {
        var errors = new List<string>();
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(IMfaActOnPolicy)))
        {
            errors.Add("AddThemiaSecondFactorAdmin needs an IMfaActOnPolicy; register one (there is no default that allows a reset).");
        }

        if (!services.Any(descriptor => descriptor.ServiceType == typeof(ISecondFactorResetObserver)))
        {
            errors.Add("AddThemiaSecondFactorAdmin needs an ISecondFactorResetObserver that ends the target's sessions; a host with no sessions registers a no-op one on purpose.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
