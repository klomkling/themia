using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Themia.AI.OpenAiCompatible.DependencyInjection;

/// <summary>DI entry point for the OpenAI-compatible completion provider.</summary>
public static class AiOpenAiCompatibleServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="OpenAiCompatibleOptions"/> (validated with <c>ValidateOnStart</c>), bridges
    /// its model names and timeout into a named <see cref="AiProviderOptions"/> keyed
    /// <see cref="AiProviderKeys.OpenAiCompatible"/> so <c>AddThemiaAi</c>'s dispatcher can resolve them,
    /// and registers <see cref="OpenAiCompatibleCompletionProvider"/> as an
    /// <see cref="IAiCompletionProvider"/>, plus its named <see cref="HttpClient"/> (see
    /// <see cref="OpenAiCompatibleCompletionProvider.HttpClientName"/>).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Sets <see cref="OpenAiCompatibleOptions.BaseUrl"/> and the rest of <see cref="OpenAiCompatibleOptions"/>.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configure"/> is null.</exception>
    public static IServiceCollection AddThemiaAiOpenAiCompatible(
        this IServiceCollection services, Action<OpenAiCompatibleOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<OpenAiCompatibleOptions>()
            .Configure(configure)
            .Validate(o => o.BaseUrl is not null, "OpenAiCompatibleOptions.BaseUrl must be set.")
            // A Bearer token on a plaintext connection is readable by every hop between the host and the
            // endpoint, and nothing at runtime would ever say so — the calls simply succeed. Plain http
            // stays allowed on loopback, because that is exactly how Ollama, LM Studio and vLLM are
            // reached and nothing leaves the machine. The message deliberately names the two options and
            // not the key's value: a validation failure is written to the host's startup log.
            .Validate(
                o => o.BaseUrl is null
                    || string.IsNullOrWhiteSpace(o.ApiKey)
                    || o.BaseUrl.Scheme != Uri.UriSchemeHttp
                    || o.BaseUrl.IsLoopback,
                $"{nameof(OpenAiCompatibleOptions)}.{nameof(OpenAiCompatibleOptions.ApiKey)} is set, but "
                + $"{nameof(OpenAiCompatibleOptions.BaseUrl)} uses plain http to a non-loopback host, "
                + "which would put the key on the wire in clear text. Use https, or drop the key for a "
                + "local endpoint that needs none.")
            .ValidateOnStart();

        // Bridges OpenAiCompatibleOptions (this package's own shape, which also carries ApiKey and
        // BaseUrl) into the named AiProviderOptions that Themia.AI's dispatcher reads via
        // IOptionsMonitor<AiProviderOptions>.Get(key) — see AiProviderOptions and
        // FailoverCompletionClient. Keeping ApiKey and BaseUrl out of AiProviderOptions is deliberate:
        // that type is shared across every provider and Themia.AI must not know this provider's auth or
        // endpoint shape.
        services.AddOptions<AiProviderOptions>(AiProviderKeys.OpenAiCompatible)
            .Configure<IOptions<OpenAiCompatibleOptions>>((providerOptions, openAiCompatible) =>
            {
                providerOptions.CompletionModel = openAiCompatible.Value.CompletionModel;
                providerOptions.TranslationModel = openAiCompatible.Value.TranslationModel;
                providerOptions.Timeout = openAiCompatible.Value.Timeout;
            });

        // Defence in depth for the API key, which OpenAiCompatibleCompletionProvider sends as an
        // Authorization: Bearer HEADER, never as part of the request URI anyone logs. Two
        // Microsoft.Extensions.Http defaults would each already keep it out of the framework's own logs:
        // LoggingHttpMessageHandler redacts header values before logging them, and it redacts a URI's
        // query string as well (System.Net.Http.UriRedactionHelper). Both are defaults this package does
        // not own — header redaction is reconfigurable per client via RedactLoggedHeaders, and URI
        // redaction is a process-wide AppContext switch (DOTNET_SYSTEM_NET_HTTP_DISABLEURIREDACTION) an
        // operator could flip while debugging something unrelated. RemoveAllLoggers() drops the
        // framework's request logging for THIS named client only, so the key's absence from logs does
        // not depend on either default holding.
        //
        // Measured 2026-09-09, mirroring Themia.AI.Gemini's Task 5 finding: deleting this line does NOT
        // redden The_api_key_never_reaches_any_log_including_the_http_clients_own — those defaults keep
        // that test green on their own. It is kept as a hedge against the defaults changing, not because
        // a test proves it load-bearing today. The load-bearing test is
        // The_api_key_travels_as_a_bearer_header_and_never_in_the_request_uri, which fails the moment
        // the key stops being sent as a header.
        services.AddHttpClient(OpenAiCompatibleCompletionProvider.HttpClientName)
            .RemoveAllLoggers();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAiCompletionProvider, OpenAiCompatibleCompletionProvider>());

        return services;
    }
}
