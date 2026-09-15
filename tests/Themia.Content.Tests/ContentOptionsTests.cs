using Xunit;

namespace Themia.Content.Tests;

public class ContentOptionsTests
{
    private static ContentOptions Options(string fallback, params string[] languages)
    {
        var options = new ContentOptions { FallbackLanguage = fallback };
        foreach (var language in languages)
        {
            options.Languages.Add(language);
        }

        return options;
    }

    [Fact]
    public void Validate_ShouldAccept_WhenFallbackIsOneOfTheLanguages() =>
        Options("th", "th", "en").Validate();

    [Fact]
    public void Validate_ShouldCompareNormalisedValues_WhenCaseAndWhitespaceDiffer() =>
        Options(" TH ", "th", "EN").Validate();

    [Fact]
    public void Validate_ShouldThrow_WhenNoLanguageIsConfigured()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Options("th").Validate());
        Assert.Contains("at least one language", error.Message);
    }

    [Fact]
    public void Validate_ShouldThrow_WhenALanguageIsBlank() =>
        Assert.Throws<InvalidOperationException>(() => Options("th", "th", "  ").Validate());

    [Fact]
    public void Validate_ShouldThrow_WhenALanguageAppearsTwiceAfterNormalising()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Options("th", "th", " TH ").Validate());
        Assert.Contains("twice", error.Message);
    }

    [Fact]
    public void Validate_ShouldThrow_WhenALanguageIsLongerThanTheColumn() =>
        Assert.Throws<InvalidOperationException>(() => Options("th", "th", new string('a', 36)).Validate());

    [Fact]
    public void Validate_ShouldThrow_WhenFallbackIsNotOneOfTheLanguages()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Options("fr", "th", "en").Validate());
        Assert.Contains("FallbackLanguage", error.Message);
    }

    [Fact]
    public void IsConfigured_ShouldMatchNormalisedLanguages()
    {
        var options = Options("th", "TH", "en");
        Assert.True(options.IsConfigured("th"));
        Assert.False(options.IsConfigured("fr"));
        Assert.Equal("th", options.NormalisedFallback);
    }
}
