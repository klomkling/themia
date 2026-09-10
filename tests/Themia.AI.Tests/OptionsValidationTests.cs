using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Themia.AI;
using Xunit;

namespace Themia.AI.Tests;

public class OptionsValidationTests
{
    // The check that would have caught the broken first draft: one model name across a failover list
    // sends one provider's model to another and fails exactly when failover fires.
    [Fact]
    public void Rejects_a_failover_provider_with_no_model_for_one_operation()
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Build.Graph(providerOptions: p =>
        {
            p.CompletionModel = "gemini-2.5-flash-lite";
            p.TranslationModel = "";                       // works for months, fails on the first translation
        }));
        Assert.Contains(nameof(AiProviderOptions.TranslationModel), ex.Message, StringComparison.Ordinal);
    }

    // Two retries x 30s = 60s against a 20s budget: the second provider is unreachable by arithmetic and
    // nothing at runtime would ever say so.
    [Fact]
    public void Rejects_a_budget_the_first_provider_alone_would_exhaust()
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Build.Graph(
            o => { o.TotalBudget = TimeSpan.FromSeconds(20); o.MaxRetriesPerProvider = 2; },
            p => p.Timeout = TimeSpan.FromSeconds(30),
            new FakeProvider()));
        Assert.Contains(nameof(AiOptions.TotalBudget), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_an_empty_failover_list_by_default()
        => Assert.Throws<OptionsValidationException>(() => Build.Graph(o => o.Failover = []));

    // A developer with no API key wants exactly this; a production host that forgot the provider package
    // wants to be told. Making it explicit is the difference.
    [Fact]
    public void Allows_an_empty_failover_list_when_declared()
    {
        using var sp = Build.Graph(o => { o.Failover = []; o.AllowNoProvider = true; });
        Assert.NotNull(sp.GetRequiredService<IAiCompletionClient>());
    }

    [Fact]
    public void Rejects_a_failover_entry_that_was_never_registered()
    {
        // Only Gemini is registered; the list also names the OpenAI-compatible provider.
        var ex = Assert.Throws<OptionsValidationException>(() => Build.Graph(
            o => o.Failover = [AiProviderKeys.Gemini, AiProviderKeys.OpenAiCompatible],
            p => { p.CompletionModel = "m"; p.TranslationModel = "m"; },
            new FakeProvider(key: AiProviderKeys.Gemini)));

        Assert.Contains(AiProviderKeys.OpenAiCompatible, ex.Message, StringComparison.Ordinal);
    }
}
