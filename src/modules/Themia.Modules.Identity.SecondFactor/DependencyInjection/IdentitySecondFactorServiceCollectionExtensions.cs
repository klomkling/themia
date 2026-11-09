using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Modules.Identity.Abstractions.Authentication;

namespace Themia.Modules.Identity.SecondFactor.DependencyInjection;

/// <summary>Registers the bridge between Themia Identity and Themia.SecondFactor.</summary>
public static class IdentitySecondFactorServiceCollectionExtensions
{
    /// <summary>
    /// Replaces the default "no second factor for anyone" policy with <c>MfaSecondFactorPolicy</c>, and registers the verify-then-complete
    /// bridge, the session proof reader and the session enrolment. Call after <c>AddThemiaIdentityAspNetCore</c> and
    /// <c>AddThemiaSecondFactor</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddThemiaIdentitySecondFactor(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<ISecondFactorSubjectResolver, SecondFactorSubjectResolver>();
        services.Replace(ServiceDescriptor.Scoped<ISecondFactorPolicy, MfaSecondFactorPolicy>());
        services.TryAddScoped<ISessionProofReader, SessionProofReader>();
        services.TryAddScoped<ISecondFactorBridge, SecondFactorBridge>();
        services.TryAddScoped<ISessionSecondFactorEnrolment, SessionSecondFactorEnrolment>();
        return services;
    }
}
