namespace Themia.AI.AspNetCore;

/// <summary>
/// The <c>POST</c> probe response: the result of exactly one real <see cref="IAiCompletionClient.CompleteAsync"/>
/// call, made through whatever the adopter registered.
/// </summary>
/// <param name="Outcome">What the call produced. See <c>Themia.AI</c>'s README for what a caller does with each value.</param>
/// <param name="Model">The model that actually answered — which failover entry served the call.</param>
/// <param name="Usage">
/// Token usage, when the provider reported it. <see langword="null"/> means the provider did not
/// report usage — never that the call was free.
/// </param>
/// <param name="Elapsed">Wall-clock time the call took, including any retry and failover the dispatcher performed.</param>
/// <param name="ProviderStatus">
/// The provider's own status text, truncated to <see cref="AiProbeEndpoints.MaxProviderStatusLength"/>.
/// This is what separates the failures that all report <see cref="AiOutcome.ProviderError"/>: every value
/// Themia's own code produces here is a fixed literal or an HTTP status number — <c>HTTP 401</c> (the key
/// is wrong), <c>HTTP 429</c> (quota), <c>provider timeout</c> (<c>AiProviderOptions.Timeout</c> is too
/// low), <c>total budget exhausted</c> (<c>AiOptions.TotalBudget</c> is too low), <c>no provider was
/// tried</c> (<c>Failover</c> names nothing registered), or a <c>finishReason</c> token. Without it an
/// adopter cannot tell those apart, which is the one question this endpoint exists to answer.
/// </param>
/// <remarks>
/// <see cref="AiCompletion.ProviderStatus"/> is free-form: <see cref="IAiCompletionProvider"/> is a public
/// seam, so a provider Themia did not write may put anything there. It is truncated rather than dropped,
/// and the truncation is a bound on response size, <em>not</em> a redaction — a provider that writes a
/// secret into its own diagnostic string has already handed it to the adopter's logs by the time this
/// endpoint sees it. The endpoint is fail-closed behind
/// <see cref="AiProbeOptions.Authorize"/>, and the adopter chose the provider.
/// </remarks>
public sealed record AiProbeCallReport(
    AiOutcome Outcome,
    string? Model,
    AiUsage? Usage,
    TimeSpan Elapsed,
    string? ProviderStatus);
