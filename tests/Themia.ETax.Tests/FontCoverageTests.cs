using Xunit;

namespace Themia.ETax.Tests;

public sealed class FontCoverageTests
{
    [Theory]
    [InlineData("สวัสดี บริษัท ตัวอย่าง จำกัด (มหาชน)")]
    [InlineData("Acme Co., Ltd. 123 /-()#&")]
    [InlineData("Café Łódź Őő Ñandú Šiauliai Ðürer")]
    [InlineData("line one\nline two\r\nline three")]
    [InlineData("")]
    public void Covers_ShouldBeTrue_WhenEveryCharacterIsInTheEmbeddedFaces(string text) =>
        Assert.True(TaxDocumentFontCoverage.Covers(text));

    [Theory]
    [InlineData("中文公司")]
    [InlineData("ひらがな")]
    [InlineData("العربية")]
    [InlineData("emoji \U0001F600")]
    [InlineData("bell\u0007")]
    [InlineData("tab\there")]
    [InlineData("lone \uD800 surrogate")]
    public void Covers_ShouldBeFalse_WhenAnyCharacterIsOutsideTheEmbeddedFaces(string text) =>
        Assert.False(TaxDocumentFontCoverage.Covers(text));

    [Theory]
    [InlineData("\u200B", "zero width space")]
    [InlineData("\u200C", "zero width non-joiner")]
    [InlineData("\u200D", "zero width joiner")]
    [InlineData("\u2060", "word joiner")]
    [InlineData("\uFEFF", "byte order mark")]
    public void Covers_ShouldBeTrue_WhenTextCarriesAZeroWidthDefaultIgnorable(string ignorable, string because) =>
        Assert.True(TaxDocumentFontCoverage.Covers($"สวัสดี{ignorable}ครับ"), because);

    [Fact]
    public void Covers_ShouldBeTrue_WhenTheTextIsNull() =>
        Assert.True(TaxDocumentFontCoverage.Covers(null));

    [Fact]
    public void Covers_ShouldRequireEveryFace_WhenEitherFaceLacksTheCharacter()
    {
        // The Bold face prints the same text as Regular, so coverage is the intersection of both cmaps.
        var regular = TaxDocumentFontCoverage.ReadCodePoints(TaxDocumentFonts.Regular);
        var bold = TaxDocumentFontCoverage.ReadCodePoints(TaxDocumentFonts.Bold);

        Assert.True(regular.Count > 500);
        foreach (var codePoint in regular.Intersect(bold))
            Assert.True(TaxDocumentFontCoverage.Covers(char.ConvertFromUtf32(codePoint)), $"U+{codePoint:X4}");
        foreach (var codePoint in regular.Except(bold).Concat(bold.Except(regular)))
            Assert.False(TaxDocumentFontCoverage.Covers(char.ConvertFromUtf32(codePoint)), $"U+{codePoint:X4}");
    }
}
