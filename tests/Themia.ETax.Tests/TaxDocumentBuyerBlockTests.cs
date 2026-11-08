using System.Net;
using System.Text.RegularExpressions;
using Xunit;

namespace Themia.ETax.Tests;

/// <summary>
/// <see cref="TaxDocumentBuyerBlock"/> must return what the PDF shows. The tests render the real template, read the buyer block back the way
/// a browser lays it out (white-space: pre-line on the v1-v3 address, normal everywhere else) and compare.
/// </summary>
public sealed class TaxDocumentBuyerBlockTests
{
    private static readonly string[] AllVersions = ["1", "2", "3", "4", "5", "6", "7"];

    public static TheoryData<string, string, string?, string?, string?> Buyers()
    {
        var data = new TheoryData<string, string, string?, string?, string?>();
        foreach (var version in AllVersions)
        {
            data.Add(version, "บริษัท ตัวอย่าง จำกัด", "99/1 หมู่ 3 ถนนสุขุมวิท", "0105536000001", "00000");
            data.Add(version, "บริษัท ตัวอย่าง จำกัด", "99/1 หมู่ 3 ถนนสุขุมวิท", "0105536000001", "00001");
            data.Add(version, "สมชาย ใจดี", null, null, null);
            data.Add(version, "สมชาย ใจดี", "1 ถนนสีลม", null, "00002");
            data.Add(version, "สมชาย ใจดี", null, "1101700203451", "00000");
            data.Add(version, "Name  with   spaces\tand tab", "99/1  หมู่ 3\nถนนสุขุมวิท", "0105536000001", "00001");
            data.Add(version, "  Padded name ", "  leading and trailing  ", "0105536000001", null);
            data.Add(version, "Name\r\nwith crlf", "line one\r\nline two\r\n\r\nline four", "0105536000001", "00003");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Buyers))]
    public void Lines_ShouldEqualTheBuyerBlockTheTemplatePrints(string version, string name, string? address, string? taxId, string? branch)
    {
        var buyer = new TaxParty { Name = name, Address = address, TaxId = taxId, Branch = branch };
        var html = WebUtility.HtmlDecode(new TaxDocumentRenderer(new TemplateVersion1GoldenTests.NoPdf()).RenderHtml(
            Samples.Receipt() with { Buyer = buyer }, version, TaxDocumentMarker.Original, null));

        var printed = PrintedBuyerLines(html, flat: TaxDocumentTemplates.PageSizeOf(version) is null);

        Assert.Equal(printed, TaxDocumentBuyerBlock.Lines(buyer, version));
    }

    [Fact]
    public void Lines_ShouldCollapseAddressRunsAndKeepNewlines_WhenTheVersionIsFlat()
    {
        var buyer = new TaxParty { Name = "A", Address = "99/1  หมู่ 3\nถนนสุขุมวิท" };

        Assert.Equal(["A", "99/1 หมู่ 3", "ถนนสุขุมวิท"], TaxDocumentBuyerBlock.Lines(buyer, "1"));
    }

    [Fact]
    public void Lines_ShouldJoinTheAddressOnOneLine_WhenTheVersionIsPaged()
    {
        var buyer = new TaxParty { Name = "A", Address = "99/1  หมู่ 3\nถนนสุขุมวิท" };

        Assert.Equal(["A", "99/1 หมู่ 3 ถนนสุขุมวิท"], TaxDocumentBuyerBlock.Lines(buyer, "6"));
    }

    [Fact]
    public void Lines_ShouldBindToTheOptionsVersion_WhenGivenTheOptions()
    {
        var buyer = new TaxParty { Name = "A", TaxId = "0105536000001", Branch = "00000" };

        Assert.Equal(TaxDocumentBuyerBlock.Lines(buyer, "6"), TaxDocumentBuyerBlock.Lines(buyer, new TaxDocumentTemplateOptions { TemplateVersion = "6" }));
        Assert.Equal(TaxDocumentBuyerBlock.Lines(buyer, "1"), TaxDocumentBuyerBlock.Lines(buyer, new TaxDocumentTemplateOptions()));
    }

    [Fact]
    public void Lines_ShouldThrow_WhenTheVersionIsNotSupported()
    {
        Assert.Throws<ArgumentException>(() => TaxDocumentBuyerBlock.Lines(new TaxParty { Name = "A" }, "8"));
    }

    [Theory]
    [InlineData("1", "สาขา / Branch 00007")]
    [InlineData("6", "สาขาที่ 00007")]
    public void Render_ShouldPrintTheSellerBranchLabel_WhenTheSellerIsABranch(string version, string expected)
    {
        var snapshot = Samples.Receipt() with { Seller = Samples.Receipt().Seller with { Branch = "00007" } };

        var html = WebUtility.HtmlDecode(new TaxDocumentRenderer(new TemplateVersion1GoldenTests.NoPdf()).RenderHtml(snapshot, version, TaxDocumentMarker.Original, null));

        Assert.Contains(expected, html, StringComparison.Ordinal);
    }

    [Fact]
    public void AbbreviatedTemplate_ShouldPrintNoBuyerBlock()
    {
        var snapshot = Samples.Receipt() with
        {
            Form = TaxDocumentForm.Abbreviated,
            Buyer = new TaxParty { Name = "ผู้ซื้อลับ", Address = "ที่อยู่ลับ", TaxId = "0105536000001", Branch = "00001" },
        };

        var html = WebUtility.HtmlDecode(new TaxDocumentRenderer(new TemplateVersion1GoldenTests.NoPdf()).RenderHtml(snapshot, "1", TaxDocumentMarker.Original, null));

        Assert.DoesNotContain("ผู้ซื้อลับ", html, StringComparison.Ordinal);
        Assert.DoesNotContain("ที่อยู่ลับ", html, StringComparison.Ordinal);
    }

    // The block as laid out by a browser. Flat (v1-v3): children of the party div after the Buyer heading, the address div is pre-line.
    // Paged (v4-v7): the name span, then the sibling divs; everything is normal white-space.
    private static IReadOnlyList<string> PrintedBuyerLines(string html, bool flat)
    {
        var lines = new List<string>();
        if (flat)
        {
            var start = html.IndexOf("<h2>ผู้ซื้อ / Buyer</h2>", StringComparison.Ordinal);
            Assert.True(start >= 0);
            var at = start + "<h2>ผู้ซื้อ / Buyer</h2>".Length;
            Match m;
            while ((m = new Regex(@"\G\s*<div([^>]*)>(.*?)</div>", RegexOptions.Singleline).Match(html, at)).Success)
            {
                var text = Regex.Replace(m.Groups[2].Value, "<[^>]+>", string.Empty);
                if (m.Groups[1].Value.Contains("address", StringComparison.Ordinal))
                    lines.AddRange(PreLine(text));
                else
                    lines.Add(Normal(text));
                at = m.Index + m.Length;
            }

            return lines;
        }

        var name = Regex.Match(html, @"<div class=""box buyer"">.*?<span class=""name"">(.*?)</span></div>", RegexOptions.Singleline);
        Assert.True(name.Success);
        lines.Add(Normal(name.Groups[1].Value));
        var position = name.Index + name.Length;
        Match next;
        while ((next = new Regex(@"\G\s*<div>(.*?)</div>", RegexOptions.Singleline).Match(html, position)).Success)
        {
            lines.Add(Normal(Regex.Replace(next.Groups[1].Value, "<[^>]+>", string.Empty)));
            position = next.Index + next.Length;
        }

        return lines;
    }

    private static string Normal(string text) => Regex.Replace(text, @"[\s ]+", " ").Trim();

    private static IEnumerable<string> PreLine(string text) =>
        Regex.Split(text, @"\r\n|\n|\r")
            .Select(line => Regex.Replace(line, @"[ \t\f ]+", " ").Trim())
            .Where(line => line.Length > 0);
}
