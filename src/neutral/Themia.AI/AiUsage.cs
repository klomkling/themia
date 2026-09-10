namespace Themia.AI;

/// <summary>Token usage reported by a provider for one call.</summary>
/// <param name="InputTokens">Tokens consumed by the prompt (<see cref="AiPrompt.System"/> plus <see cref="AiPrompt.User"/>).</param>
/// <param name="OutputTokens">Tokens consumed by the generated output.</param>
/// <remarks>
/// Populated whenever the provider reports usage, on EVERY <see cref="AiOutcome"/> — not only
/// <see cref="AiOutcome.Completed"/>. A <see cref="AiOutcome.Filtered"/> call still consumed its input
/// tokens and is billed for them; a <see cref="AiOutcome.Truncated"/> call consumed input and a full
/// output allowance. A caller recording cost from successes alone undercounts, and undercounts most on
/// the calls that failed.
/// <para>
/// On <see cref="AiCompletion"/>, <see langword="null"/> means "the provider did not report usage" —
/// never "this call was free".
/// </para>
/// </remarks>
public sealed record AiUsage(int InputTokens, int OutputTokens);
