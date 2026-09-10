namespace Themia.AI.AspNetCore;

/// <summary>
/// The <c>GET</c> probe response: what the adopter's <c>AddThemiaAi</c> / provider-package
/// configuration actually resolved to, without making a provider call. Answers "did my environment
/// variables land?" at zero cost.
/// </summary>
/// <param name="RegisteredProviders">
/// The <see cref="IAiCompletionProvider.Key"/> of every <see cref="IAiCompletionProvider"/> registered
/// in the container, regardless of whether it appears in <see cref="Failover"/>.
/// </param>
/// <param name="Failover">
/// One entry per <c>AiOptions.Failover</c> position, in order.
/// </param>
/// <param name="MaxRetriesPerProvider">Mirrors <c>AiOptions.MaxRetriesPerProvider</c>.</param>
/// <param name="TotalBudget">Mirrors <c>AiOptions.TotalBudget</c>.</param>
/// <param name="AllowNoProvider">Mirrors <c>AiOptions.AllowNoProvider</c>.</param>
public sealed record AiProbeConfigurationReport(
    IReadOnlyList<string> RegisteredProviders,
    IReadOnlyList<AiProbeFailoverEntry> Failover,
    int MaxRetriesPerProvider,
    TimeSpan TotalBudget,
    bool AllowNoProvider);

/// <summary>One <c>AiOptions.Failover</c> entry, as actually configured.</summary>
/// <param name="Key">The provider key named in <c>AiOptions.Failover</c> at this position.</param>
/// <param name="Registered">
/// Whether an <see cref="IAiCompletionProvider"/> with this <see cref="Key"/> is actually registered.
/// <see langword="false"/> means this failover entry can never be reached — the adopter named a
/// provider they never added (e.g. forgot <c>AddThemiaAiGemini</c>).
/// </param>
/// <param name="CompletionModel">The model configured for <c>AiOperation.Completion</c> on this provider.</param>
/// <param name="TranslationModel">The model configured for <c>AiOperation.Translation</c> on this provider.</param>
/// <param name="Timeout">The per-call timeout configured for this provider.</param>
public sealed record AiProbeFailoverEntry(
    string Key,
    bool Registered,
    string CompletionModel,
    string TranslationModel,
    TimeSpan Timeout);
