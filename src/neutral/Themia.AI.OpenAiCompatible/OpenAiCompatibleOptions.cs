namespace Themia.AI.OpenAiCompatible;

/// <summary>Configuration for <see cref="OpenAiCompatibleCompletionProvider"/>.</summary>
/// <remarks>
/// <see cref="CompletionModel"/>, <see cref="TranslationModel"/> and <see cref="Timeout"/> are copied
/// into a named <see cref="AiProviderOptions"/> (keyed <see cref="AiProviderKeys.OpenAiCompatible"/>) by
/// <c>AddThemiaAiOpenAiCompatible</c> (in <c>Themia.AI.OpenAiCompatible.DependencyInjection</c>), because
/// the dispatcher registered by <c>AddThemiaAi</c> only knows <see cref="AiProviderOptions"/> — it must
/// not know this provider's auth shape (<see cref="ApiKey"/>), and <see cref="AiProviderOptions"/> is
/// shared across every provider package.
/// </remarks>
public sealed class OpenAiCompatibleOptions
{
    /// <summary>
    /// The base URL of an endpoint speaking the OpenAI chat-completions shape, e.g.
    /// <c>https://api.openai.com/v1</c> or <c>http://localhost:11434/v1</c> for a local Ollama. Required
    /// — one implementation reaches OpenAI, Ollama, Groq, Cerebras, LM Studio and vLLM by changing only
    /// this value. <c>/chat/completions</c> is appended by the provider; do not include it here.
    /// </summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>
    /// The API key, sent as an <c>Authorization: Bearer</c> header. Optional — left empty, no
    /// <c>Authorization</c> header is sent, which is what a local endpoint with no auth (Ollama, LM
    /// Studio) expects; every hosted endpoint (OpenAI, Groq, Cerebras) requires this to be set.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>The model used for <see cref="AiOperation.Completion"/> (e.g. <c>gpt-4o-mini</c>, <c>qwen2.5:7b</c>).</summary>
    public string CompletionModel { get; set; } = string.Empty;

    /// <summary>The model used for <see cref="AiOperation.Translation"/>.</summary>
    public string TranslationModel { get; set; } = string.Empty;

    /// <summary>
    /// The per-call timeout. Defaults to 20 seconds, which keeps <c>AiOptions.MaxRetriesPerProvider</c>'s
    /// default of 2 comfortably inside <c>AiOptions.TotalBudget</c>'s default of 45 seconds, so a host
    /// that only sets <see cref="BaseUrl"/> and the two model names still passes startup validation.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);
}
