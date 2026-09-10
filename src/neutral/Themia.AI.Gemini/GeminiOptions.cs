namespace Themia.AI.Gemini;

/// <summary>Configuration for <see cref="GeminiCompletionProvider"/>.</summary>
/// <remarks>
/// <see cref="CompletionModel"/>, <see cref="TranslationModel"/> and <see cref="Timeout"/> are copied
/// into a named <see cref="AiProviderOptions"/> (keyed <see cref="AiProviderKeys.Gemini"/>) by
/// <c>AddThemiaAiGemini</c> (in <c>Themia.AI.Gemini.DependencyInjection</c>), because the dispatcher
/// registered by <c>AddThemiaAi</c> only knows <see cref="AiProviderOptions"/> — it must not know
/// Gemini's auth shape (<see cref="ApiKey"/>), and <see cref="AiProviderOptions"/> is shared across
/// every provider package.
/// </remarks>
public sealed class GeminiOptions
{
    /// <summary>
    /// The Gemini API key. Sent as the <c>x-goog-api-key</c> request header — Google's documented header
    /// form — and never as a <c>key</c> query parameter, so it is not part of a request URI that a proxy,
    /// an access log or an HTTP-client log could record.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>The model used for <see cref="AiOperation.Completion"/> (e.g. <c>gemini-2.5-flash-lite</c>).</summary>
    public string CompletionModel { get; set; } = string.Empty;

    /// <summary>The model used for <see cref="AiOperation.Translation"/>.</summary>
    public string TranslationModel { get; set; } = string.Empty;

    /// <summary>
    /// The per-call timeout. Defaults to 20 seconds, which keeps <c>AiOptions.MaxRetriesPerProvider</c>'s
    /// default of 2 comfortably inside <c>AiOptions.TotalBudget</c>'s default of 45 seconds, so a host
    /// that only sets <see cref="ApiKey"/> and the two model names still passes startup validation.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);
}
