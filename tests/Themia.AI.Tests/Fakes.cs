using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Themia.AI;
using Themia.AI.DependencyInjection;
using Themia.AI.Internal;

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

/// <summary>
/// Throws instead of producing a completion — what a provider looks like when the transport fails and
/// nothing maps it: a refused connection to a stopped local server, a DNS failure, a body that is not
/// JSON. <see cref="FakeProvider"/> cannot express this, which is why no test could see that an
/// exception from the first provider ended the call instead of failing over.
/// </summary>
internal sealed class ThrowingProvider(Exception? exception = null, string key = AiProviderKeys.Gemini)
    : IAiCompletionProvider
{
    public string Key { get; } = key;

    public int Calls { get; private set; }

    public Task<AiCompletion> CompleteAsync(
        string model, AiPrompt prompt, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Calls++;
        cancellationToken.ThrowIfCancellationRequested();
        throw exception ?? new HttpRequestException("Connection refused (localhost:11434)");
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

/// <summary>
/// Always returns the same <see cref="AiCompletion"/>, ignoring the request. For tests exercising the
/// <see cref="AiOutcome"/> → <see cref="TranslationOutcome"/> mapping (Task 7), where only the outcome
/// matters and not what was asked of the client.
/// </summary>
internal sealed class StubCompletionClient(AiOutcome outcome, string? text = null) : IAiCompletionClient
{
    public Task<AiCompletion> CompleteAsync(
        AiOperation operation, AiPrompt prompt, CancellationToken cancellationToken = default)
        => Task.FromResult(new AiCompletion(outcome, text, null, "stub-model", null));
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

    /// <summary>
    /// Builds a container wired the same way as <see cref="Graph"/>, but resolves the real
    /// <see cref="IAiCompletionClient"/> (the Task 4 dispatcher) instead of <see cref="RecordingClient"/>,
    /// so a test can exercise retry, failover and the total budget end to end.
    /// </summary>
    /// <param name="providers">
    /// The providers to register, in failover order. Each entry gets its own synthetic key
    /// (<c>"provider-0"</c>, <c>"provider-1"</c>, …) independent of <see cref="IAiCompletionProvider.Key"/>,
    /// so a test can pass two <see cref="FakeProvider"/>s that share the same default
    /// <see cref="AiProviderKeys.Gemini"/> key — as most of these tests do — without them colliding as one
    /// registration. <see cref="AiOptions.Failover"/> is set to those synthetic keys, in the given order.
    /// Defaults to a single <see cref="FakeProvider"/> when none are given.
    /// </param>
    /// <param name="options">Applied after a baseline built from <paramref name="providers"/>' synthetic keys.</param>
    /// <param name="providerOptions">
    /// Applied, per registered provider, after a baseline of <c>"completion-model"</c> /
    /// <c>"translation-model"</c> and a 1-second timeout — distinct model names so a test can assert which
    /// one the dispatcher resolved. The 1-second baseline (rather than <see cref="Graph"/>'s 10) keeps
    /// <c>MaxRetriesPerProvider &#215; Timeout</c> inside a small overridden <see cref="AiOptions.TotalBudget"/>
    /// without every budget test having to restate the provider timeout too.
    /// </param>
    internal static IAiCompletionClient Client(
        Action<AiOptions>? options = null,
        Action<AiProviderOptions>? providerOptions = null,
        params IAiCompletionProvider[] providers)
    {
        var services = new ServiceCollection();
        var registered = providers.Length == 0 ? [new FakeProvider()] : providers;
        var keys = registered.Select((_, i) => $"provider-{i}").ToArray();

        services.AddThemiaAi(o =>
        {
            o.Failover = keys;
            options?.Invoke(o);
        });

        for (var i = 0; i < registered.Length; i++)
        {
            services.AddSingleton<IAiCompletionProvider>(new KeyedProvider(registered[i], keys[i]));
            services.Configure<AiProviderOptions>(keys[i], po =>
            {
                po.CompletionModel = "completion-model";
                po.TranslationModel = "translation-model";
                po.Timeout = TimeSpan.FromSeconds(1);
            });

            if (providerOptions is not null)
            {
                services.Configure(keys[i], providerOptions);
            }
        }

        return services.BuildServiceProvider().GetRequiredService<IAiCompletionClient>();
    }

    /// <summary>
    /// Builds a <see cref="TranslationService"/> whose <see cref="IAiCompletionClient"/> always returns
    /// <paramref name="outcome"/> with <paramref name="text"/>, for tests exercising the
    /// <c>AiOutcome</c> → <c>TranslationOutcome</c> mapping (Task 7).
    /// </summary>
    internal static ITextTranslationService Service(AiOutcome outcome, string? text = null)
        => Service(new StubCompletionClient(outcome, text));

    /// <summary>
    /// Builds a <see cref="TranslationService"/> wrapping <paramref name="client"/> directly, for tests
    /// asserting whether or how the client was called (Task 7) — e.g. with a <see cref="RecordingClient"/>.
    /// </summary>
    internal static ITextTranslationService Service(IAiCompletionClient client)
        => new TranslationService(client, NullLogger<TranslationService>.Instance);

    /// <summary>Delegates to <paramref name="inner"/> but reports <paramref name="key"/>, so tests can register the same underlying <see cref="FakeProvider"/> more than once under distinct failover positions.</summary>
    private sealed class KeyedProvider(IAiCompletionProvider inner, string key) : IAiCompletionProvider
    {
        public string Key { get; } = key;

        public Task<AiCompletion> CompleteAsync(
            string model, AiPrompt prompt, TimeSpan timeout, CancellationToken cancellationToken = default)
            => inner.CompleteAsync(model, prompt, timeout, cancellationToken);
    }
}
