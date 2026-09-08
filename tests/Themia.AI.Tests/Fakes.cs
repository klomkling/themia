using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Themia.AI;
using Themia.AI.DependencyInjection;

namespace Themia.AI.Tests;

/// <summary>
/// Shared test fakes for <c>Themia.AI.Tests</c>. Defined once, here, because Tasks 2, 3, 4 and 7 of the
/// Themia.AI plan all call these and a helper invented three times acquires three shapes.
/// </summary>
/// <remarks>
/// <see cref="Build"/> is filled in incrementally as the plan's later tasks land: <c>Build.Graph</c>
/// needs <c>AiOptions</c>/<c>AiProviderOptions</c> (Task 3) and the DI registration extension, and
/// <c>Build.Client</c> needs the failover dispatcher (Task 4) registered as <see cref="IAiCompletionClient"/>.
/// <c>Build.Graph</c> lands with Task 3; <see cref="FakeProvider"/>, <see cref="RecordingClient"/> and
/// <see cref="Build.Prompt"/> were usable from Task 1. Keep the parameter order of every member below
/// stable: later tasks are written against it.
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

    /// <summary>
    /// Builds a container with <c>AddThemiaAi</c> and one or more <see cref="IAiCompletionProvider"/>s
    /// registered, then eagerly resolves <see cref="AiOptions"/> so a bad configuration throws from
    /// this call — the same failure <c>ValidateOnStart</c> produces when a real host starts, without
    /// needing a full <c>IHost</c> here.
    /// </summary>
    /// <param name="options">
    /// Applied after a test baseline of <c>Failover = [AiProviderKeys.Gemini]</c>, so a test only states
    /// what it means to change.
    /// </param>
    /// <param name="providerOptions">
    /// Applied, per registered provider, after a baseline of a non-empty model for every operation and
    /// a 10-second timeout — comfortably inside the default <see cref="AiOptions.TotalBudget"/> — so a
    /// test only states the one field it means to break.
    /// </param>
    /// <param name="providers">
    /// The providers to register. Defaults to a single <see cref="FakeProvider"/> keyed
    /// <see cref="AiProviderKeys.Gemini"/> when none are given.
    /// </param>
    internal static ServiceProvider Graph(
        Action<AiOptions>? options = null,
        Action<AiProviderOptions>? providerOptions = null,
        params IAiCompletionProvider[] providers)
    {
        var services = new ServiceCollection();

        services.AddThemiaAi(o =>
        {
            o.Failover = [AiProviderKeys.Gemini];
            options?.Invoke(o);
        });

        var registered = providers.Length == 0 ? [new FakeProvider()] : providers;

        foreach (var provider in registered)
        {
            services.AddSingleton<IAiCompletionProvider>(provider);
            services.Configure<AiProviderOptions>(provider.Key, po =>
            {
                po.CompletionModel = "test-model";
                po.TranslationModel = "test-model";
                po.Timeout = TimeSpan.FromSeconds(10);
            });

            if (providerOptions is not null)
            {
                services.Configure(provider.Key, providerOptions);
            }
        }

        // Task 3 does not register a real IAiCompletionClient (Task 8 does); RecordingClient stands in
        // so callers that only need "the graph built" (e.g. Allows_an_empty_failover_list_when_declared)
        // can resolve one.
        services.AddSingleton<IAiCompletionClient, RecordingClient>();

        var serviceProvider = services.BuildServiceProvider();
        _ = serviceProvider.GetRequiredService<IOptions<AiOptions>>().Value;

        return serviceProvider;
    }
}
