using Themia.AI;

namespace Themia.AI.Tests;

/// <summary>
/// Shared test fakes for <c>Themia.AI.Tests</c>. Defined once, here, because Tasks 2, 3, 4 and 7 of the
/// Themia.AI plan all call these and a helper invented three times acquires three shapes.
/// </summary>
/// <remarks>
/// <see cref="Build"/> is filled in incrementally as the plan's later tasks land: <c>Build.Graph</c>
/// needs <c>AiOptions</c>/<c>AiProviderOptions</c> (Task 3) and the DI registration extension, and
/// <c>Build.Client</c> needs the failover dispatcher (Task 4) registered as <see cref="IAiCompletionClient"/>.
/// Neither type exists yet at Task 1, so only the parts usable today — <see cref="FakeProvider"/>,
/// <see cref="RecordingClient"/> and <see cref="Build.Prompt"/> — are defined here. Keep the parameter
/// order of every member below stable: later tasks are written against it.
/// </remarks>
internal sealed class FakeProvider(
    AiOutcome outcome = AiOutcome.Completed,
    string? text = null,
    TimeSpan? delay = null,
    string key = AiProviderKeys.Gemini) : IAiCompletionProvider
{
    public string Key { get; } = key;
    public int Calls { get; private set; }
    public string? LastModel { get; private set; }

    public async Task<AiCompletion> CompleteAsync(
        string model, AiPrompt prompt, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Calls++;
        LastModel = model;
        if (delay is { } d) await Task.Delay(d, cancellationToken);
        return new AiCompletion(outcome, text, new AiUsage(1, 1), model, null);
    }
}

/// <summary>Records calls without producing one. For asserting that a path did NOT reach a provider.</summary>
internal sealed class RecordingClient : IAiCompletionClient
{
    public int Calls { get; private set; }

    public Task<AiCompletion> CompleteAsync(
        AiOperation operation, AiPrompt prompt, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(new AiCompletion(AiOutcome.Completed, "", null, "m", null));
    }
}

internal static class Build
{
    internal static AiPrompt Prompt(string user = "hello") => new() { User = user };
}
