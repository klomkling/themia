using System.Diagnostics;
using Themia.AI;
using Xunit;

namespace Themia.AI.Tests;

public class FailoverTests
{
    [Fact]
    public async Task Fails_over_to_the_next_provider_on_provider_limit()
    {
        var first = new FakeProvider(AiOutcome.ProviderLimit);
        var second = new FakeProvider(AiOutcome.Completed, text: "ok");
        var result = await Build.Client(providers: [first, second]).CompleteAsync(AiOperation.Completion, Build.Prompt(), default);

        Assert.Equal(AiOutcome.Completed, result.Outcome);
        Assert.Equal(1, second.Calls);
    }

    // A safety refusal is about the content. The next provider refuses it too, so failing over spends a
    // second call to receive the same answer.
    [Fact]
    public async Task Does_not_fail_over_on_filtered()
    {
        var first = new FakeProvider(AiOutcome.Filtered);
        var second = new FakeProvider(AiOutcome.Completed);
        var result = await Build.Client(providers: [first, second]).CompleteAsync(AiOperation.Completion, Build.Prompt(), default);

        Assert.Equal(AiOutcome.Filtered, result.Outcome);
        Assert.Equal(0, second.Calls);          // the assertion that matters
    }

    // The per-call timeout alone does not bound what a caller waits: retries multiply it and failover
    // repeats it. This is the test for the budget that replaced that reasoning.
    [Fact]
    public async Task The_total_budget_bounds_retries_and_failover_together()
    {
        var slow = new FakeProvider(delay: TimeSpan.FromSeconds(10));
        var clock = Stopwatch.StartNew();
        await Build.Client(o => o.TotalBudget = TimeSpan.FromSeconds(2), providers: [slow, slow]).CompleteAsync(AiOperation.Completion, Build.Prompt(), default);
        Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task A_provider_timeout_surfaces_as_provider_error_and_fails_over()
    {
        // The provider hangs past its own Timeout. A hang is not an exception, so without the dispatcher
        // converting it there is no ProviderError and failover never fires — the caller just waits.
        var hangs = new FakeProvider(delay: TimeSpan.FromSeconds(30));
        var second = new FakeProvider(AiOutcome.Completed, text: "ok");

        var result = await Build.Client(
                o => o.TotalBudget = TimeSpan.FromSeconds(5),
                p => p.Timeout = TimeSpan.FromMilliseconds(200),
                hangs, second)
            .CompleteAsync(AiOperation.Completion, Build.Prompt(), default);

        Assert.Equal(AiOutcome.Completed, result.Outcome);
        Assert.Equal(1, second.Calls);
    }

    // Distinct from a timeout: the caller stopped caring, so burning a second provider's quota is wrong.
    [Fact]
    public async Task Caller_cancellation_propagates_and_does_not_fail_over()
    {
        using var cts = new CancellationTokenSource();
        var second = new FakeProvider(AiOutcome.Completed);
        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Build.Client(providers: [new FakeProvider(delay: TimeSpan.FromSeconds(5)), second])
                .CompleteAsync(AiOperation.Completion, Build.Prompt(), cts.Token));
        Assert.Equal(0, second.Calls);
    }

    // Without this the per-operation model configuration is unreachable: every call would resolve
    // CompletionModel and TranslationModel would be a setting nobody reads — set, validated, and ignored.
    [Fact]
    public async Task A_translation_call_resolves_the_translation_model()
    {
        var provider = new FakeProvider(AiOutcome.Completed);
        await Build.Client(providers: provider).CompleteAsync(AiOperation.Translation, Build.Prompt(), default);
        Assert.Equal("translation-model", provider.LastModel);
    }
}
