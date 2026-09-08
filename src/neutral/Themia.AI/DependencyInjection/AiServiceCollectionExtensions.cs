using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Themia.AI.Internal;

namespace Themia.AI.DependencyInjection;

/// <summary>
/// Registers <see cref="AiOptions"/> (validated with <c>ValidateOnStart</c> against the whole provider
/// &#215; operation matrix, design §6) and the <see cref="IAiCompletionClient"/> dispatcher that resolves
/// models, retries, fails over and bounds every call to <see cref="AiOptions.TotalBudget"/>.
/// </summary>
/// <remarks>
/// Does not register a masker or a translation service — those are added to this same method by a later
/// task. A provider package (e.g. a future <c>AddThemiaAiGemini</c>) registers its own
/// <see cref="IAiCompletionProvider"/> and a named <see cref="AiProviderOptions"/> alongside this call.
/// </remarks>
public static class AiServiceCollectionExtensions
{
    /// <summary>Registers <see cref="AiOptions"/> with <c>ValidateOnStart</c>, and the failover dispatcher as <see cref="IAiCompletionClient"/>.</summary>
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

        // TryAdd-based: a host that already configured logging is unaffected, and a bare host still gets
        // a working (no-op) ILogger<T> instead of a DI resolution failure the first time the dispatcher
        // is used. Matches Themia.Challenges' AddThemiaChallenges.
        services.AddLogging();

        services.TryAddSingleton<IAiCompletionClient>(sp =>
        {
            var logger = sp.GetService<ILogger<FailoverCompletionClient>>()
                ?? NullLogger<FailoverCompletionClient>.Instance;

            return new FailoverCompletionClient(
                sp.GetRequiredService<IEnumerable<IAiCompletionProvider>>(),
                sp.GetRequiredService<IOptions<AiOptions>>(),
                sp.GetRequiredService<IOptionsMonitor<AiProviderOptions>>(),
                logger);
        });

        return services;
    }
}
