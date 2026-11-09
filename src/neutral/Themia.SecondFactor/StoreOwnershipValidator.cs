using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Themia.Totp;
using Themia.WebAuthn;

namespace Themia.SecondFactor;

/// <summary>
/// Fails startup when the TOTP replay store or the WebAuthn challenge store that will be resolved is not this layer's. Both
/// <c>AddThemiaTotp</c> and <c>AddThemiaWebAuthn</c> keep an earlier registration, so an app that registered its own would silently
/// bypass the single-use code record and the passkey ceremonies kept here. The message names types only, never values.
/// </summary>
internal sealed class StoreOwnershipValidator(IServiceCollection services) : IValidateOptions<SecondFactorOptions>
{
    public ValidateOptionsResult Validate(string? name, SecondFactorOptions options)
    {
        var errors = new List<string>();
        Check<ITotpReplayStore, MfaTotpReplayStore>(errors);
        Check<IWebAuthnChallengeStore, CeremonyBackedWebAuthnStore>(errors);
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    // The last registration is the one that resolves.
    private void Check<TService, TOwn>(List<string> errors)
    {
        var resolved = services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(TService));
        if (resolved?.ImplementationType != typeof(TOwn))
        {
            errors.Add(
                $"{typeof(TService).Name} resolves to {resolved?.ImplementationType?.Name ?? "a factory or instance"}, not {typeof(TOwn).Name}. " +
                "Themia.SecondFactor must own this store; remove the other registration.");
        }
    }
}
