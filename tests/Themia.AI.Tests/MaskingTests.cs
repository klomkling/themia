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
