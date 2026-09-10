using Themia.AI;
using Xunit;

namespace Themia.AI.Tests;

public class MaskingTests
{
    private static readonly AiTextMasker Masker = new();

    [Fact]
    public void Restores_values_after_the_model_reorders_them()
    {
        var masked = Masker.Mask("บ้านของคุณสมชาย ติดต่อ 081-234-5678", ["สมชาย", "081-234-5678"]);
        // Translation reorders constantly; unique numbered tokens are what makes this survive.
        var modelOutput = masked.Text.Replace("บ้านของคุณ", "House of ");
        var restored = masked.Restore(modelOutput);

        Assert.Contains("สมชาย", restored, StringComparison.Ordinal);
        Assert.Contains("081-234-5678", restored, StringComparison.Ordinal);
    }

    [Fact]
    public void Throws_when_the_model_dropped_a_token()
    {
        var masked = Masker.Mask("a X b", ["X"]);
        Assert.Throws<InvalidOperationException>(() => masked.Restore("the model rewrote everything"));
    }

    [Fact]
    public void Throws_when_a_token_came_back_twice()
    {
        var masked = Masker.Mask("a X b", ["X"]);
        var token = masked.Tokens.Keys.Single();
        Assert.Throws<InvalidOperationException>(() => masked.Restore($"{token} and again {token}"));
    }

    // The failure that matters most for an API whose job is masking: .NET alternation takes the FIRST
    // alternative that matches at a position, not the longest, so listing the given name before the full
    // name masked "สมชาย" and sent " ใจดี" — the family name, the PII — to the provider in clear. The
    // caller did nothing wrong; they listed both values.
    [Fact]
    public void The_longest_matching_value_is_masked_not_the_first_listed()
    {
        var masked = Masker.Mask("ติดต่อ สมชาย ใจดี", ["สมชาย", "สมชาย ใจดี"]);

        Assert.DoesNotContain("ใจดี", masked.Text, StringComparison.Ordinal);
        Assert.Equal("ติดต่อ สมชาย ใจดี", masked.Restore(masked.Text));
    }

    // The token id is captured from untrusted text by (\d+), which bounds neither its length nor its
    // value. This threw OverflowException straight out of Mask.
    [Fact]
    public void An_id_too_large_for_an_int_does_not_throw()
    {
        var masked = Masker.Mask("""<x id="99999999999999999999"/> and NAME""", ["NAME"]);

        Assert.Equal("""<x id="99999999999999999999"/> and NAME""", masked.Restore(masked.Text));
    }

    // Worse than the throw above, because nothing was raised at all: int.MaxValue parsed, maxId + 1
    // wrapped to int.MinValue, the generated token read <x id="-2147483648"/>, MaskToken.Pattern could
    // not match it, and Restore left the token sitting in the output — the user shown a placeholder in
    // place of their own data, silently.
    [Fact]
    public void An_id_of_int_max_does_not_silently_drop_the_masked_value()
    {
        var masked = Masker.Mask("""<x id="2147483647"/> and NAME""", ["NAME"]);

        Assert.DoesNotContain("NAME", masked.Text, StringComparison.Ordinal);   // it really was masked
        Assert.Equal("""<x id="2147483647"/> and NAME""", masked.Restore(masked.Text));
    }

    // The text being masked is untrusted by construction. A listing description containing a literal token
    // would otherwise collide, Restore would see it twice and throw, and ONE LINE in a description would
    // make that listing permanently untranslatable — a denial an ordinary user can trigger.
    [Fact]
    public void Source_text_that_already_contains_a_token_still_round_trips()
    {
        var masked = Masker.Mask("""literal <x id="1"/> here and NAME""", ["NAME"]);
        Assert.Equal("""literal <x id="1"/> here and NAME""", masked.Restore(masked.Text));
    }
}
