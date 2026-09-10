namespace Themia.AI;

/// <summary>
/// One <see cref="AiOptions.Failover"/> entry's model names and timeout. Registered per provider key
/// (e.g. one instance named <see cref="AiProviderKeys.Gemini"/>, another named
/// <see cref="AiProviderKeys.OpenAiCompatible"/>) — never shared across providers, because two
/// different endpoints never share a model name. Design §6.
/// </summary>
public sealed class AiProviderOptions
{
    /// <summary>
    /// The model this provider uses for <see cref="AiOperation.Completion"/>. Startup validation
    /// requires this to be non-empty for every provider named in <see cref="AiOptions.Failover"/>.
    /// </summary>
    public string CompletionModel { get; set; } = string.Empty;

    /// <summary>
    /// The model this provider uses for <see cref="AiOperation.Translation"/>. Startup validation
    /// requires this to be non-empty for every provider named in <see cref="AiOptions.Failover"/> — the
    /// check that catches a failover list where one provider never received its translation model.
    /// </summary>
    public string TranslationModel { get; set; } = string.Empty;

    /// <summary>
    /// The per-call timeout for this provider. Startup validation requires this to be positive. A hang
    /// past this maps to <see cref="AiOutcome.ProviderError"/> so it can fail over, rather than
    /// occupying the caller for <c>HttpClient</c>'s 100-second default.
    /// </summary>
    public TimeSpan Timeout { get; set; }
}
