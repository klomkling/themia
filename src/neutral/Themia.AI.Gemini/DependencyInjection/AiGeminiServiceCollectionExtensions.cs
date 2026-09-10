using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Themia.AI.Gemini.DependencyInjection;

/// <summary>DI entry point for the Gemini completion provider.</summary>
public static class AiGeminiServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="GeminiOptions"/> (validated with <c>ValidateOnStart</c>), bridges its model
    /// names and timeout into a named <see cref="AiProviderOptions"/> keyed
    /// <see cref="AiProviderKeys.Gemini"/> so <c>AddThemiaAi</c>'s dispatcher can resolve them, and
    /// registers <see cref="GeminiCompletionProvider"/> as an <see cref="IAiCompletionProvider"/>, plus
    /// its named <see cref="HttpClient"/> (<see cref="GeminiCompletionProvider.HttpClientName"/>).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Sets <see cref="GeminiOptions.ApiKey"/> and the rest of <see cref="GeminiOptions"/>.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configure"/> is null.</exception>
    public static IServiceCollection AddThemiaAiGemini(
        this IServiceCollection services, Action<GeminiOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<GeminiOptions>()
            .Configure(configure)
            .Validate(o => !string.IsNullOrWhiteSpace(o.ApiKey), "GeminiOptions.ApiKey must be set.")
            .ValidateOnStart();

        // Bridges GeminiOptions (this package's own shape, which also carries ApiKey) into the named
        // AiProviderOptions that Themia.AI's dispatcher reads via IOptionsMonitor<AiProviderOptions>.Get(key)
        // — see AiProviderOptions and FailoverCompletionClient. Keeping ApiKey out of AiProviderOptions is
        // deliberate: that type is shared across every provider and Themia.AI must not know Gemini's auth
        // shape.
        services.AddOptions<AiProviderOptions>(AiProviderKeys.Gemini)
            .Configure<IOptions<GeminiOptions>>((providerOptions, gemini) =>
            {
                providerOptions.CompletionModel = gemini.Value.CompletionModel;
                providerOptions.TranslationModel = gemini.Value.TranslationModel;
                providerOptions.Timeout = gemini.Value.Timeout;
            });

        // Defence in depth for the API key, which GeminiCompletionProvider sends as an x-goog-api-key
        // HEADER rather than a ?key= query parameter, so it is never part of a request URI anyone logs.
        // Two Microsoft.Extensions.Http defaults would each already keep it out of the framework's own
        // logs: LoggingHttpMessageHandler redacts header values before logging them, and it redacts a
        // URI's query string as well (System.Net.Http.UriRedactionHelper). Both are defaults this
        // package does not own — header redaction is reconfigurable per client via RedactLoggedHeaders,
        // and URI redaction is a process-wide AppContext switch
        // (DOTNET_SYSTEM_NET_HTTP_DISABLEURIREDACTION) an operator could flip while debugging something
        // unrelated. RemoveAllLoggers() drops the framework's request logging for THIS named client
        // only, so the key's absence from logs does not depend on either default holding.
        //
        // Measured 2026-09-09: deleting this line does NOT redden
        // The_api_key_never_reaches_any_log_including_the_http_clients_own — those defaults keep that
        // test green on their own. It is kept as a hedge against the defaults changing, not because a
        // test proves it load-bearing today. The load-bearing test is
        // The_api_key_travels_as_a_header_and_never_in_the_request_uri, which fails the moment the key
        // goes back into the URI.
        services.AddHttpClient(GeminiCompletionProvider.HttpClientName)
            .RemoveAllLoggers();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAiCompletionProvider, GeminiCompletionProvider>());

        return services;
    }
}
