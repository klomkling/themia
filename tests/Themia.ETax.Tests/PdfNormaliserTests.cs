using System.Text;
using Xunit;

namespace Themia.ETax.Tests;

public sealed class PdfNormaliserTests
{
    private static readonly DateOnly IssueDate = new(2026, 10, 14);

    private static byte[] Pdf(string creation, string? modification, byte[]? tail = null)
    {
        var head = $"%PDF-1.4\n<< /Producer (Skia/PDF m131) /CreationDate ({creation}) " +
                   (modification is null ? "" : $"/ModDate ({modification}) ") + ">>\n";
        return Encoding.Latin1.GetBytes(head).Concat(tail ?? [0xFF, 0x00, 0x29, 0x28, 0x80]).ToArray();
    }

    [Fact]
    public void NormaliseDates_ShouldWriteTheIssueDateIntoBothFields_WhenBothArePresent()
    {
        var result = TaxDocumentPdfNormaliser.NormaliseDates(
            Pdf("D:20260101120000+00'00'", "D:20260101120005+00'00'"), IssueDate);

        var text = Encoding.Latin1.GetString(result);
        Assert.Contains("/CreationDate (D:20261014000000+00'00')", text);
        Assert.Contains("/ModDate (D:20261014000000+00'00')", text);
    }

    [Fact]
    public void NormaliseDates_ShouldChangeNothingElse_WhenTheDatesAreRewritten()
    {
        var input = Pdf("D:20260101120000+00'00'", "D:20260101120005+00'00'", [0x01, 0xFE, 0x29, 0x00]);

        var result = TaxDocumentPdfNormaliser.NormaliseDates(input, IssueDate);

        Assert.Equal(input.Length, result.Length);
        var differing = Enumerable.Range(0, input.Length).Where(i => input[i] != result[i]).ToArray();
        var text = Encoding.Latin1.GetString(input);
        var allowed = new[] { "/CreationDate (", "/ModDate (" }
            .SelectMany(k => Enumerable.Range(text.IndexOf(k, StringComparison.Ordinal) + k.Length, 23))
            .ToHashSet();
        Assert.All(differing, i => Assert.Contains(i, allowed));
    }

    [Fact]
    public void NormaliseDates_ShouldMakeRendersFromDifferentTimesIdentical()
    {
        var a = TaxDocumentPdfNormaliser.NormaliseDates(Pdf("D:20260101120000+00'00'", "D:20260101120000+00'00'"), IssueDate);
        var b = TaxDocumentPdfNormaliser.NormaliseDates(Pdf("D:20260301235959+00'00'", "D:20260301235959+00'00'"), IssueDate);

        Assert.Equal(a, b);
    }

    [Fact]
    public void NormaliseDates_ShouldNotMutateTheInput()
    {
        var input = Pdf("D:20260101120000+00'00'", null);
        var copy = (byte[])input.Clone();

        TaxDocumentPdfNormaliser.NormaliseDates(input, IssueDate);

        Assert.Equal(copy, input);
    }

    [Fact]
    public void NormaliseDates_ShouldAcceptAMissingModDate()
    {
        var result = TaxDocumentPdfNormaliser.NormaliseDates(Pdf("D:20260101120000+00'00'", null), IssueDate);

        Assert.Contains("/CreationDate (D:20261014000000+00'00')", Encoding.Latin1.GetString(result));
    }

    [Fact]
    public void NormaliseDates_ShouldFailLoud_WhenThereIsNoCreationDate() =>
        Assert.Throws<InvalidOperationException>(
            () => TaxDocumentPdfNormaliser.NormaliseDates(Encoding.Latin1.GetBytes("%PDF-1.4\n<< /Producer (x) >>"), IssueDate));

    [Fact]
    public void NormaliseDates_ShouldFailLoud_WhenADateHasADifferentLength() =>
        Assert.Throws<InvalidOperationException>(
            () => TaxDocumentPdfNormaliser.NormaliseDates(Pdf("D:20260101120000Z", null), IssueDate));
}
