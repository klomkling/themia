using Themia.AI;

namespace Themia.AI.AspNetCore.Tests;

/// <summary>A no-network <see cref="IAiCompletionProvider"/> for exercising <c>MapThemiaAiProbe</c> without a live provider.</summary>
internal sealed class FakeAiCompletionProvider(
    string key = AiProviderKeys.Gemini,
    AiOutcome outcome = AiOutcome.Completed,
    string? text = "pong",
    AiUsage? usage = null,
    string? providerStatus = null) : IAiCompletionProvider
{
    public string Key { get; } = key;

    public int Calls { get; private set; }

    public string? LastModel { get; private set; }

    public Task<AiCompletion> CompleteAsync(
        string model, AiPrompt prompt, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Calls++;
        LastModel = model;
        return Task.FromResult(new AiCompletion(outcome, text, usage ?? new AiUsage(3, 5), model, providerStatus));
    }
}
