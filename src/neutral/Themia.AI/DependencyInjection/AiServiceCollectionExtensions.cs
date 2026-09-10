using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Themia.AI.Internal;

namespace Themia.AI.DependencyInjection;

/// <summary>
/// Registers <see cref="AiOptions"/> (validated with <c>ValidateOnStart</c> against the whole provider
/// &#215; operation matrix, design §6), the <see cref="IAiCompletionClient"/> dispatcher that resolves
/// models, retries, fails over and bounds every call to <see cref="AiOptions.TotalBudget"/>, the default
/// <see cref="IAiTextMasker"/>, and <see cref="ITextTranslationService"/>.
/// </summary>
/// <remarks>
/// A provider package (e.g. <c>AddThemiaAiGemini</c>, <c>AddThemiaAiOpenAiCompatible</c>) registers its
/// own <see cref="IAiCompletionProvider"/> and a named <see cref="AiProviderOptions"/> alongside this
/// call — this method knows nothing about any specific provider.
/// </remarks>
public static class AiServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="AiOptions"/> with <c>ValidateOnStart</c>, the failover dispatcher as
    /// <see cref="IAiCompletionClient"/>, the default <see cref="IAiTextMasker"/>, and
    /// <see cref="ITextTranslationService"/>.
    /// </summary>
    /// <remarks>
    /// Every registration here uses <c>TryAdd*</c>, so a caller's own registration of any of these
    /// services — made before or after this call — wins, and calling <c>AddThemiaAi</c> more than once
    /// (e.g. from two modules that both depend on it) is a no-op the second time rather than a duplicate
    /// registration.
    /// </remarks>
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

        services.TryAddSingleton<IAiTextMasker, AiTextMasker>();

        services.TryAddSingleton<ITextTranslationService>(sp =>
        {
            var logger = sp.GetService<ILogger<TranslationService>>()
                ?? NullLogger<TranslationService>.Instance;

            return new TranslationService(sp.GetRequiredService<IAiCompletionClient>(), logger);
        });

        return services;
    }
}
