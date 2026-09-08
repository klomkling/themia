namespace Themia.AI;

/// <summary>The result of one <see cref="IAiCompletionClient.CompleteAsync"/> or <see cref="IAiCompletionProvider.CompleteAsync"/> call.</summary>
/// <param name="Outcome">What happened. Never <see cref="AiOutcome.Unspecified"/> from a real provider.</param>
/// <param name="Text">
/// The generated text when <paramref name="Outcome"/> is <see cref="AiOutcome.Completed"/> or
/// <see cref="AiOutcome.Truncated"/>; otherwise <see langword="null"/>.
/// </param>
/// <param name="Usage">
/// Token usage, populated whenever the provider reports it on EVERY outcome. <see langword="null"/>
/// means the provider did not report usage, never that the call was free.
/// </param>
/// <param name="Model">The model that actually served the call, for logging and diagnosis.</param>
/// <param name="ProviderStatus">The provider's own status text, for logging and diagnosis. Not part of the contract's meaning — only <paramref name="Outcome"/> is.</param>
public sealed record AiCompletion(
    AiOutcome Outcome, string? Text, AiUsage? Usage, string? Model, string? ProviderStatus);
