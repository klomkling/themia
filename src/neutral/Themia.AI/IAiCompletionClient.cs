namespace Themia.AI;

/// <summary>What a consumer calls. One registration; the implementation dispatches across providers.</summary>
/// <remarks>
/// Distinct from <see cref="IAiCompletionProvider"/> on purpose: the dispatcher implements this
/// interface and resolves the model per provider per operation, applies retry, failover and a total
/// budget across every attempt. Collapsing the two would mean every provider re-implementing dispatch,
/// or the dispatcher being indistinguishable from a provider at the DI container.
/// </remarks>
public interface IAiCompletionClient
{
    /// <summary>Completes <paramref name="prompt"/> for <paramref name="operation"/>, dispatching across the configured providers.</summary>
    /// <param name="operation">Which configured model this call resolves to.</param>
    /// <param name="prompt">The instructions and content.</param>
    /// <param name="cancellationToken">Cancels the call. Propagates as <see cref="OperationCanceledException"/> and does not fail over.</param>
    Task<AiCompletion> CompleteAsync(
        AiOperation operation, AiPrompt prompt, CancellationToken cancellationToken = default);
}

/// <summary>What a provider package implements.</summary>
/// <remarks>
/// A provider talks to one endpoint with a model it is told, and knows nothing about retry, failover
/// or budgets — those live in the dispatcher registered as <see cref="IAiCompletionClient"/>. Providers
/// are resolved as <c>IEnumerable&lt;IAiCompletionProvider&gt;</c> and matched by <see cref="Key"/>
/// against the configured failover list.
/// </remarks>
public interface IAiCompletionProvider
{
    /// <summary>Identifies this provider in configuration. Compared ordinally, case-insensitively.</summary>
    string Key { get; }

    /// <summary>Makes one call to this provider's endpoint.</summary>
    /// <param name="model">The model to use, resolved by the dispatcher from configuration.</param>
    /// <param name="prompt">The instructions and content.</param>
    /// <param name="timeout">The per-call timeout. A hang past this maps to <see cref="AiOutcome.ProviderError"/> so it can fail over.</param>
    /// <param name="cancellationToken">Cancels the call. Propagates as <see cref="OperationCanceledException"/>.</param>
    Task<AiCompletion> CompleteAsync(
        string model, AiPrompt prompt, TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>The keys of the providers Themia ships.</summary>
/// <remarks>
/// A plain string, not an enum: the core must not enumerate every provider that could exist, or adding
/// one would mean changing this package and an adopter could never supply their own (Bedrock, Vertex,
/// a company gateway).
/// </remarks>
public static class AiProviderKeys
{
    /// <summary>The key of the Gemini provider (<c>Themia.AI.Gemini</c>).</summary>
    public const string Gemini = "gemini";

    /// <summary>The key of the OpenAI-compatible provider (<c>Themia.AI.OpenAiCompatible</c>), for any endpoint speaking the OpenAI chat-completions shape (Ollama, vLLM, Azure OpenAI, OpenAI itself).</summary>
    public const string OpenAiCompatible = "openai-compatible";
}
