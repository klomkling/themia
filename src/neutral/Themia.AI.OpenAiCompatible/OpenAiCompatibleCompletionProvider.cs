using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

namespace Themia.AI.OpenAiCompatible;

/// <summary>
/// <see cref="IAiCompletionProvider"/> over the OpenAI chat-completions REST shape. One implementation
/// reaches OpenAI, Ollama, Groq, Cerebras, LM Studio and vLLM by changing
/// <see cref="OpenAiCompatibleOptions.BaseUrl"/>.
/// </summary>
/// <remarks>
/// Maps HTTP status first — <c>429</c> to <see cref="AiOutcome.ProviderLimit"/>, any other non-success
/// status to <see cref="AiOutcome.ProviderError"/> — then, on a successful HTTP response, maps the first
/// choice's <c>finish_reason</c>: <c>stop</c> to <see cref="AiOutcome.Completed"/>, <c>length</c> to
/// <see cref="AiOutcome.Truncated"/>, and <c>content_filter</c> to <see cref="AiOutcome.Filtered"/>.
/// Anything else — including a malformed <c>200</c> payload — maps to <see cref="AiOutcome.ProviderError"/>.
/// <c>usage</c> is read whenever the response carries it, on every outcome.
/// </remarks>
public sealed class OpenAiCompatibleCompletionProvider(
    IHttpClientFactory httpClientFactory, IOptions<OpenAiCompatibleOptions> openAiCompatibleOptions) : IAiCompletionProvider
{
    /// <summary>The named <see cref="HttpClient"/> this provider resolves via <see cref="IHttpClientFactory"/>.</summary>
    public const string HttpClientName = "Themia.AI.OpenAiCompatible";

    private static readonly JsonSerializerOptions SerializeOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <inheritdoc />
    public string Key => AiProviderKeys.OpenAiCompatible;

    /// <inheritdoc />
    public async Task<AiCompletion> CompleteAsync(
        string model, AiPrompt prompt, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(prompt);

        var options = openAiCompatibleOptions.Value;
        var httpClient = httpClientFactory.CreateClient(HttpClientName);
        var requestUri = BuildRequestUri(options.BaseUrl);
        var payload = JsonSerializer.Serialize(BuildRequestBody(model, prompt), SerializeOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }

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

        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            // A 200 response with no choices is a provider-side surprise, not a match — there is no
            // "malformed" outcome in the contract, so this falls to ProviderError like any other
            // unexpected response (mirrors Themia.AI.Gemini's handling of a candidate-less payload).
            return new AiCompletion(AiOutcome.ProviderError, null, usage, model, "no choices");
        }

        var choice = choices[0];
        var finishReason = choice.TryGetProperty("finish_reason", out var finishReasonElement) &&
            finishReasonElement.ValueKind == JsonValueKind.String
            ? finishReasonElement.GetString() ?? string.Empty
            : string.Empty;

        return finishReason switch
        {
            "stop" => new AiCompletion(AiOutcome.Completed, ExtractText(choice), usage, model, finishReason),
            "length" => new AiCompletion(AiOutcome.Truncated, ExtractText(choice), usage, model, finishReason),
            "content_filter" => new AiCompletion(AiOutcome.Filtered, null, usage, model, finishReason),
            _ => new AiCompletion(AiOutcome.ProviderError, null, usage, model, finishReason),
        };
    }

    private static string? ExtractText(JsonElement choice)
    {
        if (!choice.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = content.GetString();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static AiUsage? ExtractUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var inputTokens = usage.TryGetProperty("prompt_tokens", out var promptTokens) &&
            promptTokens.ValueKind == JsonValueKind.Number
            ? promptTokens.GetInt32()
            : 0;

        var outputTokens = usage.TryGetProperty("completion_tokens", out var completionTokens) &&
            completionTokens.ValueKind == JsonValueKind.Number
            ? completionTokens.GetInt32()
            : 0;

        return new AiUsage(inputTokens, outputTokens);
    }

    private static Uri BuildRequestUri(Uri? baseUrl)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return new Uri($"{baseUrl.ToString().TrimEnd('/')}/chat/completions");
    }

    private static ChatCompletionRequest BuildRequestBody(string model, AiPrompt prompt)
    {
        var messages = new List<ChatMessage>();
        if (!string.IsNullOrEmpty(prompt.System))
        {
            messages.Add(new ChatMessage { Role = "system", Content = prompt.System });
        }

        messages.Add(new ChatMessage { Role = "user", Content = prompt.User });

        return new ChatCompletionRequest
        {
            Model = model,
            Messages = messages,
            MaxTokens = prompt.MaxOutputTokens,
            Temperature = prompt.Temperature,
        };
    }

    private sealed class ChatCompletionRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("messages")]
        public required List<ChatMessage> Messages { get; init; }

        [JsonPropertyName("max_tokens")]
        public int? MaxTokens { get; init; }

        [JsonPropertyName("temperature")]
        public double? Temperature { get; init; }
    }

    private sealed class ChatMessage
    {
        [JsonPropertyName("role")]
        public required string Role { get; init; }

        [JsonPropertyName("content")]
        public required string Content { get; init; }
    }
}
