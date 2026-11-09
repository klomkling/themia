using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Themia.SecondFactor;

// Fails host start when the admin reset is on but nobody supplied the act-on rule: there is no default that allows.
internal sealed class AdminPolicyValidator(IServiceCollection services) : IValidateOptions<SecondFactorOptions>
{
    public ValidateOptionsResult Validate(string? name, SecondFactorOptions options) =>
        services.Any(descriptor => descriptor.ServiceType == typeof(IMfaActOnPolicy))
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("AddThemiaSecondFactorAdmin needs an IMfaActOnPolicy; register one (there is no default that allows a reset).");
}
