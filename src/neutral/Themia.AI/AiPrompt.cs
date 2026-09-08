namespace Themia.AI;

/// <summary>The input to one <see cref="IAiCompletionClient.CompleteAsync"/> call.</summary>
/// <remarks>
/// Deliberately carries no model name: configuration is the only source of the model (see
/// <c>AiProviderOptions</c>). A caller-supplied name cannot be right for two providers with different
/// model names in one failover list.
/// </remarks>
public sealed record AiPrompt
{
    /// <summary>Instructions for the model. NEVER put untrusted text here — see <see cref="User"/>.</summary>
    public string? System { get; init; }

    /// <summary>The content being worked on. Untrusted input belongs here, not in <see cref="System"/>.</summary>
    public required string User { get; init; }

    /// <summary>Caps the output length. <see langword="null"/> uses the provider's default.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>Sampling temperature. <see langword="null"/> uses the provider's default.</summary>
    public double? Temperature { get; init; }
}
