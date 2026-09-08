using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Themia.AI.DependencyInjection;

/// <summary>
/// Registers <see cref="AiOptions"/>, validated with <c>ValidateOnStart</c> against the whole provider
/// &#215; operation matrix (design §6).
/// </summary>
/// <remarks>
/// This is the first version of <c>AddThemiaAi</c>: options registration and validation only. It does
/// not register a masker, a dispatcher, or a translation service — those are added to this same method
/// by a later task, once <see cref="IAiCompletionClient"/> has an implementation to register. A provider
/// package (e.g. a future <c>AddThemiaAiGemini</c>) registers its own <see cref="IAiCompletionProvider"/>
/// and a named <see cref="AiProviderOptions"/> alongside this call.
/// </remarks>
public static class AiServiceCollectionExtensions
{
    /// <summary>Registers <see cref="AiOptions"/> with <c>ValidateOnStart</c>.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Sets <see cref="AiOptions.Failover"/> and the rest of the shared settings.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddThemiaAi(
        this IServiceCollection services, Action<AiOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<AiOptions>()
            .Configure(configure)
            .ValidateOnStart();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AiOptions>, AiOptionsValidator>());

        return services;
    }
}
