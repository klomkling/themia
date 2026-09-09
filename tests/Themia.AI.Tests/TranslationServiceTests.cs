using Themia.AI;
using Xunit;
using static Themia.AI.Tests.Build;

namespace Themia.AI.Tests;

public class TranslationServiceTests
{
    // One row per line of the spec's mapping table.
    [Theory]
    [InlineData(AiOutcome.Completed,     TranslationOutcome.Translated)]
    [InlineData(AiOutcome.Truncated,     TranslationOutcome.Incomplete)]
    [InlineData(AiOutcome.Filtered,      TranslationOutcome.Unavailable)]
    [InlineData(AiOutcome.ProviderLimit, TranslationOutcome.Unavailable)]
    [InlineData(AiOutcome.ProviderError, TranslationOutcome.Unavailable)]
    public async Task Maps_every_completion_outcome(AiOutcome completion, TranslationOutcome expected)
    {
        var result = await Service(completion).TranslateAsync("สวัสดี", "th-TH", "en-US", default);
        Assert.Equal(expected, result.Outcome);
    }

    // The row a reflexive implementation gets wrong: Truncated carries non-empty text, so mapping it to
    // Translated is the natural mistake — and the caller then stores a sentence that stops mid-word into
    // ProposalTranslation, labelled complete and marked fresh.
    [Fact]
    public async Task Truncated_is_never_reported_as_translated()
    {
        var result = await Service(AiOutcome.Truncated, text: "This sentence stops mid-w").TranslateAsync("x", "th-TH", "en-US", default);
        Assert.Equal(TranslationOutcome.Incomplete, result.Outcome);
        Assert.NotNull(result.Text);           // the text IS the point; it just must not be stored
    }

    // Returning the input would be the helpful thing and is exactly what the placeholder did. A caller
    // writing row.Title = result.Text without reading the outcome must FAIL, not persist Thai as English.
    [Fact]
    public async Task Unavailable_carries_a_null_text()
    {
        // The stub returns a NON-null text on the failed call on purpose. With a null one, an
        // implementation that passed completion.Text straight through — the natural regression, and half
        // a step from echoing the source back the way the placeholder did — would still satisfy
        // Assert.Null and this test would police nothing.
        var result = await Service(AiOutcome.ProviderError, text: "partial output from a failed call")
            .TranslateAsync("สวัสดี", "th-TH", "en-US", default);

        Assert.Equal(TranslationOutcome.Unavailable, result.Outcome);
        Assert.Null(result.Text);
    }

    [Fact]
    public async Task Same_language_returns_the_input_without_calling_a_provider()
    {
        var client = new RecordingClient();
        var result = await Service(client).TranslateAsync("สวัสดี", "th-TH", "th-TH", default);

        Assert.Equal(TranslationOutcome.SameLanguage, result.Outcome);
        Assert.Equal("สวัสดี", result.Text);
        Assert.Equal(0, client.Calls);
    }

    // A call that can only return nothing still costs a call, and per-call cost is the stated constraint.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_input_calls_no_provider(string text)
    {
        var client = new RecordingClient();
        await Service(client).TranslateAsync(text, "th-TH", "en-US", default);
        Assert.Equal(0, client.Calls);
    }

    // The placeholder normalised unknown codes to th-TH, so asking for fr-FR returned Thai and reported
    // success. Codes pass through; the provider's rejection surfaces as Unavailable.
    [Fact]
    public async Task An_unsupported_language_is_not_silently_defaulted()
    {
        var result = await Service(AiOutcome.ProviderError).TranslateAsync("hello", "en-US", "xx-XX", default);
        Assert.Equal(TranslationOutcome.Unavailable, result.Outcome);
        Assert.Null(result.Text);
    }
}
