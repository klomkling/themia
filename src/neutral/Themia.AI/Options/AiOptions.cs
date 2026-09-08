namespace Themia.AI;

/// <summary>Failover order and the shared budget spanning every provider one call may reach.</summary>
/// <remarks>
/// Per-provider settings — model names and timeout — live on <see cref="AiProviderOptions"/>, one
/// instance per <see cref="Failover"/> entry, keyed by <see cref="IAiCompletionProvider.Key"/> — not
/// here. Design §6: a model belongs to a provider, not to an operation, so failing over from Gemini to
/// an OpenAI-compatible endpoint can never send Gemini's model name to it.
/// </remarks>
public sealed class AiOptions
{
    /// <summary>
    /// Provider keys in preference order (see <see cref="AiProviderKeys"/>). Empty by default, which
    /// fails startup validation unless <see cref="AllowNoProvider"/> is set — an empty list is a
    /// decision, not an omission.
    /// </summary>
    public string[] Failover { get; set; } = [];

    /// <summary>
    /// The wall-clock budget for one <see cref="IAiCompletionClient.CompleteAsync"/> call, spanning
    /// every retry and every failover. Defaults to 45 seconds. Startup validation rejects a
    /// configuration whose worst case on the first provider alone — <see cref="MaxRetriesPerProvider"/>
    /// times its <see cref="AiProviderOptions.Timeout"/> — cannot fit inside this budget, because that
    /// means the second provider is unreachable by arithmetic.
    /// </summary>
    public TimeSpan TotalBudget { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// How many times to retry one provider on <see cref="AiOutcome.ProviderError"/> before failing
    /// over to the next entry in <see cref="Failover"/>. Defaults to 2.
    /// </summary>
    public int MaxRetriesPerProvider { get; set; } = 2;

    /// <summary>
    /// Declares that running with no configured provider is intentional. When <see langword="false"/>
    /// (the default), an empty <see cref="Failover"/> fails startup validation — the failure a
    /// production host that forgot to register a provider package should see. When
    /// <see langword="true"/>, an empty <see cref="Failover"/> passes validation, for a developer with
    /// no API key who wants the application to start anyway.
    /// </summary>
    public bool AllowNoProvider { get; set; }
}
