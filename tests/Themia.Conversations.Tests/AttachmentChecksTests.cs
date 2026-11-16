using Themia.Conversations.Internal;
using Xunit;

namespace Themia.Conversations.Tests;

public sealed class AttachmentChecksTests
{
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0];
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0];
    private static readonly byte[] PdfBytes = "%PDF-1.7 body"u8.ToArray();
    private static readonly byte[] WebpBytes = "RIFF\0\0\0\0WEBPVP8 "u8.ToArray();

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/webp")]
    [InlineData("application/pdf")]
    public void Matches_ShouldAccept_WhenBytesCarryTheDeclaredSignature(string type)
    {
        var bytes = type switch { "image/png" => PngBytes, "image/jpeg" => JpegBytes, "image/webp" => WebpBytes, _ => PdfBytes };

        Assert.True(AttachmentSniffer.Matches(type, bytes));
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/webp")]
    public void Matches_ShouldRefuse_WhenAPdfIsDeclaredAsAnImage(string type) =>
        Assert.False(AttachmentSniffer.Matches(type, PdfBytes));

    [Fact]
    public void Matches_ShouldRefuse_WhenAnImageIsDeclaredAsPdf_OrTheTypeHasNoSignature()
    {
        Assert.False(AttachmentSniffer.Matches("application/pdf", PngBytes));
        Assert.False(AttachmentSniffer.Matches("text/html", "<html>"u8.ToArray()));
    }

    [Fact]
    public void Matches_ShouldRefuseAShortRiffThatIsNotWebp() =>
        Assert.False(AttachmentSniffer.Matches("image/webp", "RIFF\0\0\0\0WAVEfmt "u8.ToArray()));

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData(@"C:\Users\x\evil.exe", "evil.exe")]
    [InlineData(".htaccess", "htaccess")]
    [InlineData("a\r\nb.png", "ab.png")]
    [InlineData("we<ir>d|name?.pdf", "weirdname.pdf")]
    [InlineData("   ", "attachment")]
    [InlineData(null, "attachment")]
    [InlineData("รูป.png", "รูป.png")]
    public void Sanitize_ShouldKeepOnlyASafeLeafName(string? input, string expected) =>
        Assert.Equal(expected, AttachmentFileName.Sanitize(input, "application/x-unknown"));

    [Theory]
    [InlineData("x.hta", "application/pdf", "x.pdf")]
    [InlineData("x.html", "application/pdf", "x.pdf")]
    [InlineData("x.svg", "application/pdf", "x.pdf")]
    [InlineData("invoice", "application/pdf", "invoice.pdf")]
    [InlineData("a.b.exe", "application/pdf", "a.b.pdf")]
    [InlineData("p.jpg", "image/webp", "p.webp")]
    [InlineData("p.png", "image/jpeg", "p.jpg")]
    [InlineData("รูป.html", "image/png", "รูป.png")]
    [InlineData("...", "application/pdf", "attachment.pdf")]
    public void Sanitize_ShouldForceTheExtensionOfTheStoredType(string input, string storedType, string expected) =>
        Assert.Equal(expected, AttachmentFileName.Sanitize(input, storedType));

    [Fact]
    public void Sanitize_ShouldDropFormatCharacters_SoARightToLeftOverrideCannotDisguiseTheExtension()
    {
        var result = AttachmentFileName.Sanitize("invoice\u202Elmth.pdf\u200B", "application/pdf");

        Assert.Equal("invoicelmth.pdf", result);
        Assert.DoesNotContain(result, c => char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format);
    }

    [Fact]
    public void Sanitize_ShouldDropUnpairedSurrogates_AndKeepPairedOnes()
    {
        // A Fact, not a Theory: xunit's data serialization would turn a lone surrogate into U+FFFD before the test saw it.
        var cases = new (string Input, string Expected)[]
        {
            ("a\uD800b.pdf", "ab.pdf"),
            ("a\uDC00b.pdf", "ab.pdf"),
            ("\uD800\uDC00x.pdf", "\uD800\uDC00x.pdf"),
            ("x\uDC00\uD800.pdf", "x.pdf"),
        };
        var utf8 = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true);

        foreach (var (input, expected) in cases)
        {
            var result = AttachmentFileName.Sanitize(input, "application/pdf");

            Assert.Equal(expected, result);
            utf8.GetBytes(result);
        }
    }

    [Fact]
    public void Sanitize_ShouldKeepTheForcedExtension_WhenTheNameIsAtTheLengthCap()
    {
        var result = AttachmentFileName.Sanitize(new string('a', 400) + ".html", "application/pdf");

        Assert.Equal(255, result.Length);
        Assert.EndsWith(".pdf", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_ShouldCapTheLength_WithoutSplittingASurrogatePair()
    {
        var name = new string('a', 254) + "😀";

        var result = AttachmentFileName.Sanitize(name, "application/x-unknown");

        Assert.True(result.Length <= 255);
        Assert.False(char.IsHighSurrogate(result[^1]));
    }

    [Fact]
    public void ContentDisposition_ShouldBeAnAttachment_WithAnAsciiFallbackAndTheEncodedName()
    {
        var header = AttachmentFileName.ContentDisposition("รูป \"x\".png");

        Assert.StartsWith("attachment; filename=\"", header);
        Assert.DoesNotContain("รูป", header[..header.IndexOf("filename*", StringComparison.Ordinal)]);
        Assert.Contains("filename*=UTF-8''", header);
        Assert.DoesNotContain("\"x\"", header);
    }
}
