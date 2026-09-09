using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

namespace Themia.AI.Gemini;

/// <summary>
/// <see cref="IAiCompletionProvider"/> over the Google Gemini <c>generateContent</c> REST API.
/// </summary>
/// <remarks>
/// Maps HTTP status first — <c>429</c> to <see cref="AiOutcome.ProviderLimit"/>, any other non-success
/// status to <see cref="AiOutcome.ProviderError"/> — then, on a successful HTTP response, maps the
/// candidate's <c>finishReason</c>: <c>STOP</c> to <see cref="AiOutcome.Completed"/>, <c>MAX_TOKENS</c>
/// to <see cref="AiOutcome.Truncated"/>, and every safety-refusal reason (<c>SAFETY</c>,
/// <c>RECITATION</c>, <c>BLOCKLIST</c>, <c>PROHIBITED_CONTENT</c>, <c>SPII</c>) to
/// <see cref="AiOutcome.Filtered"/>. A prompt-level block (<c>promptFeedback.blockReason</c>, no
/// candidates at all) also maps to <see cref="AiOutcome.Filtered"/>. Anything else — including a
/// malformed <c>200</c> payload — maps to <see cref="AiOutcome.ProviderError"/>.
/// <c>usageMetadata</c> is read whenever the response carries it, on every outcome.
/// </remarks>
public sealed class GeminiCompletionProvider(
    IHttpClientFactory httpClientFactory, IOptions<GeminiOptions> geminiOptions) : IAiCompletionProvider
{
    /// <summary>The named <see cref="HttpClient"/> this provider resolves via <see cref="IHttpClientFactory"/>.</summary>
    public const string HttpClientName = "Themia.AI.Gemini";

    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";

    // Google's documented header form for the Generative Language REST API
    // (https://ai.google.dev/gemini-api/docs/api-key, read 2026-09-09). The `?key=` query form also
    // works, but puts the secret in the request URI, where every proxy, access log and HTTP-client log
    // on the path can record it. A header keeps it out of all of them by construction, rather than
    // depending on each of those layers choosing to redact a query string.
    private const string ApiKeyHeader = "x-goog-api-key";


    private static readonly JsonSerializerOptions SerializeOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <inheritdoc />
    public string Key => AiProviderKeys.Gemini;

    /// <inheritdoc />
    public async Task<AiCompletion> CompleteAsync(
        string model, AiPrompt prompt, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(prompt);

        var httpClient = httpClientFactory.CreateClient(HttpClientName);
        var requestUri = BuildRequestUri(model);
        var payload = JsonSerializer.Serialize(BuildRequestBody(prompt), SerializeOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(ApiKeyHeader, geminiOptions.Value.ApiKey);

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var outcome = response.StatusCode == HttpStatusCode.TooManyRequests
                ? AiOutcome.ProviderLimit
                : AiOutcome.ProviderError;
            return new AiCompletion(outcome, null, null, model, $"HTTP {(int)response.StatusCode}");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapResponse(document.RootElement, model);
        }
    }

    private static AiCompletion MapResponse(JsonElement root, string model)
    {
        var usage = ExtractUsage(root);

        if (root.TryGetProperty("promptFeedback", out var promptFeedback) &&
            promptFeedback.ValueKind == JsonValueKind.Object &&
            promptFeedback.TryGetProperty("blockReason", out _))
        {
            return new AiCompletion(AiOutcome.Filtered, null, usage, model, "promptFeedback.blockReason");
        }

        if (!root.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array ||
            candidates.GetArrayLength() == 0)
        {
            // A 200 response with no candidates and no promptFeedback block is a provider-side surprise,
            // not a match — there is no "malformed" outcome in the contract, so this falls to
            // ProviderError like any other unexpected response (mirrors Themia.Geo.Google's handling of
            // a malformed OK payload).
            return new AiCompletion(AiOutcome.ProviderError, null, usage, model, "no candidates");
        }

        var candidate = candidates[0];
        var finishReason = candidate.TryGetProperty("finishReason", out var finishReasonElement)
            ? finishReasonElement.GetString() ?? string.Empty
            : string.Empty;

        return finishReason switch
        {
            "STOP" => new AiCompletion(AiOutcome.Completed, ExtractText(candidate), usage, model, finishReason),
            "MAX_TOKENS" => new AiCompletion(AiOutcome.Truncated, ExtractText(candidate), usage, model, finishReason),
            "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" =>
                new AiCompletion(AiOutcome.Filtered, null, usage, model, finishReason),
            _ => new AiCompletion(AiOutcome.ProviderError, null, usage, model, finishReason),
        };
    }

    private static string? ExtractText(JsonElement candidate)
    {
        if (!candidate.TryGetProperty("content", out var content) ||
            !content.TryGetProperty("parts", out var parts) ||
            parts.ValueKind != JsonValueKind.Array ||
            parts.GetArrayLength() == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
            {
                builder.Append(textElement.GetString());
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static AiUsage? ExtractUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usageMetadata", out var usageMetadata) || usageMetadata.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var inputTokens = usageMetadata.TryGetProperty("promptTokenCount", out var promptTokens) &&
            promptTokens.ValueKind == JsonValueKind.Number
            ? promptTokens.GetInt32()
            : 0;

        var outputTokens = usageMetadata.TryGetProperty("candidatesTokenCount", out var candidateTokens) &&
            candidateTokens.ValueKind == JsonValueKind.Number
            ? candidateTokens.GetInt32()
            : 0;

        return new AiUsage(inputTokens, outputTokens);
    }

    private static Uri BuildRequestUri(string model)
        => new($"{BaseUrl}{Uri.EscapeDataString(model)}:generateContent");

    private static GenerateContentRequest BuildRequestBody(AiPrompt prompt) => new()
    {
        Contents = [new GeminiContent { Role = "user", Parts = [new GeminiPart { Text = prompt.User }] }],
        SystemInstruction = string.IsNullOrEmpty(prompt.System)
            ? null
            : new GeminiContent { Parts = [new GeminiPart { Text = prompt.System }] },
        GenerationConfig = prompt.MaxOutputTokens is null && prompt.Temperature is null
            ? null
            : new GenerationConfig { MaxOutputTokens = prompt.MaxOutputTokens, Temperature = prompt.Temperature },
    };

    private sealed class GenerateContentRequest
    {
        [JsonPropertyName("contents")]
        public required List<GeminiContent> Contents { get; init; }

        [JsonPropertyName("systemInstruction")]
        public GeminiContent? SystemInstruction { get; init; }

        [JsonPropertyName("generationConfig")]
        public GenerationConfig? GenerationConfig { get; init; }
    }

    private sealed class GeminiContent
    {
        [JsonPropertyName("role")]
        public string? Role { get; init; }

        [JsonPropertyName("parts")]
        public required List<GeminiPart> Parts { get; init; }
    }

    private sealed class GeminiPart
    {
        [JsonPropertyName("text")]
        public required string Text { get; init; }
    }

    private sealed class GenerationConfig
    {
        [JsonPropertyName("maxOutputTokens")]
        public int? MaxOutputTokens { get; init; }

        [JsonPropertyName("temperature")]
        public double? Temperature { get; init; }
    }
}
