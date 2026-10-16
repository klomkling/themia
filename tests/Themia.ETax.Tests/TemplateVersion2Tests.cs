using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Themia.ETax.Tests;

/// <summary>
/// Template version "2" is template "1" plus the amount in Thai words under the total. Version "1" stays frozen (its goldens are untouched);
/// version "2" is pinned the same way, and "A failure here means: ship template version 3, never update the hash" applies to it from now on.
/// </summary>
public sealed class TemplateVersion2Tests
{
    public static TheoryData<string, string> Documents => new()
    {
        { "receipt-withheld-original", "49322f24ec5d2454ce92a2997dc7172dc70c1e7423f6821de666b3d7f0395f67" },
        { "receipt-copy", "615f750af0e684267d999185cd08e7bdbe7d9fd40a3feb569f4704de6edd49f9" },
        { "credit-full-copy", "262c5702fd728c4deed54409ddf0f60dc8443d4d23aab3fa816633527a006c02" },
        { "credit-adjustment-copy", "2fd09400a9d652a86cbe4bb18160b4c0e428c995f926a3d3872f0713f01c9b57" },
        { "debit-adjustment-copy", "775e89e21c40d78ac0329f69789a7712f28ac05b0a9257d1eb0da04e4e1b0bef" },
        { "replacement-original", "f49a0eda34538ed346bb60de367098ccc7ee2d18c52c0041d5c9c05163c47fab" },
        { "receipt-cancelled-copy", "0974a9e7000fe5d4f9e186f437f056e8161f29e2898d9b8b2efe32ab37091dfc" },
    };

    [Theory]
    [MemberData(nameof(Documents))]
    public void Html_ShouldMatchTheGolden_BecauseVersion2MustNeverChange(string name, string expectedSha256)
    {
        var (snapshot, marker) = TemplateVersion1GoldenTests.Fixture(name);
        var html = new TaxDocumentRenderer(new TemplateVersion1GoldenTests.NoPdf()).RenderHtml(snapshot, "2", marker);

        Assert.Equal(expectedSha256, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html))).ToLowerInvariant());
    }

    [Fact]
    public void Version2_ShouldBeFrozen_BecauseStoredDocumentsWillBeRenderedFromIt()
    {
        Assert.Equal(
            "b8dd7fbf30dd2d7d477c6caf533288d6fec38f283e518f1f6f604ebc8f15fe61",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TaxDocumentTemplates.Get("2")))).ToLowerInvariant());
    }

    private static string HtmlOf(TaxDocumentSnapshot snapshot, string version) =>
        System.Net.WebUtility.HtmlDecode(new TaxDocumentRenderer(new TemplateVersion1GoldenTests.NoPdf())
            .RenderHtml(snapshot, version, TaxDocumentMarker.Original));

    [Fact]
    public void Version2_ShouldPrintTheGrandTotalInWords_UnderTheTotals()
    {
        var html = HtmlOf(Samples.Receipt(), "2"); // 1,070.00 incl. VAT

        Assert.Contains("<div class=\"in-words\">ตัวอักษร / In words: หนึ่งพันเจ็ดสิบบาทถ้วน</div>", html);
        Assert.True(html.IndexOf("in-words\">", StringComparison.Ordinal) > html.IndexOf("class=\"totals\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Version2_ShouldPrintTheCreditedAmountInWords_OnACreditNote()
    {
        var html = HtmlOf(Samples.AdjustmentNote(TaxDocumentKind.CreditNote, 200_000, 150_000), "2"); // credited 500.00

        Assert.Contains("In words: ห้าร้อยบาทถ้วน", html);
    }

    [Fact]
    public void Version1_ShouldNotPrintTheAmountInWords_InAnyCase()
    {
        var html = HtmlOf(Samples.Receipt(), "1");

        Assert.DoesNotContain("In words", html);
        Assert.DoesNotContain("หนึ่งพันเจ็ดสิบบาทถ้วน", html);
    }

    [Fact]
    public void TheVersionFixesTheEtConvention_Version2ReadsEtAndVersion3ReadsNeung()
    {
        var snapshot = Samples.Receipt() with { Totals = Samples.Receipt().Totals with { InclVatSatang = 10_100 } };

        Assert.Contains("In words: หนึ่งร้อยเอ็ดบาทถ้วน", HtmlOf(snapshot, "2"));
        Assert.Contains("In words: หนึ่งร้อยหนึ่งบาทถ้วน", HtmlOf(snapshot, "3"));
    }

    [Fact]
    public void Version3_ShouldDifferFromVersion2_OnlyInTheWordsWhenTheConventionMatters()
    {
        var plain = Samples.Receipt(); // 1,070.00: no units digit of 1 in the last group

        Assert.Equal(HtmlOf(plain, "2"), HtmlOf(plain, "3"));
    }

    [Fact]
    public void Version2_ShouldDifferFromVersion1_OnlyByTheAmountInWordsBlock()
    {
        var one = HtmlOf(Samples.Receipt(), "1");
        var two = HtmlOf(Samples.Receipt(), "2");

        var withoutBlock = System.Text.RegularExpressions.Regex.Replace(two, "<div class=\"in-words\">.*?</div>\n", string.Empty);
        var withoutStyle = System.Text.RegularExpressions.Regex.Replace(withoutBlock, @"\.in-words \{[^\n]*\n", string.Empty);
        Assert.Equal(one, withoutStyle);
    }

    [Fact]
    public void Templates_ShouldSupportVersions1To3Only()
    {
        Assert.True(TaxDocumentTemplates.IsSupported("1"));
        Assert.True(TaxDocumentTemplates.IsSupported("2"));
        Assert.True(TaxDocumentTemplates.IsSupported("3"));
        Assert.False(TaxDocumentTemplates.IsSupported("4"));
        Assert.False(TaxDocumentTemplates.IsSupported(null));
        Assert.Equal("1", TaxDocumentTemplates.CurrentVersion);
        Assert.Equal("3", TaxDocumentTemplates.LatestVersion);
        Assert.Equal(TaxDocumentTemplates.Get("2"), TaxDocumentTemplates.Get("3"));
        Assert.Throws<ArgumentException>(() => TaxDocumentTemplates.Get("4"));
    }

    [Fact]
    public async Task Service_ShouldIssueNewDocumentsUnderVersion1_UnlessTheAdopterOptsIn()
    {
        var plain = new Flow();
        var optedIn = new Flow(templates: new TaxDocumentTemplateOptions { TemplateVersion = "2" });

        Assert.Equal("1", (await plain.IssuedAsync()).TemplateVersion);
        Assert.Equal("2", (await optedIn.IssuedAsync()).TemplateVersion);
    }

    [Fact]
    public async Task Service_ShouldRenderTheSameBytes_AfterTheAdopterChangesTheTemplateVersion()
    {
        var snapshot = Samples.Receipt() with
        {
            Lines = [new TaxDocumentLine { Description = "Boost package", AmountExVatSatang = 9_439 }],
            Totals = Samples.Receipt().Totals with { ExVatSatang = 9_439, VatSatang = 661, InclVatSatang = 10_100, WithheldSatang = 0, NetReceivedSatang = 10_100 },
        };
        var before = new Flow(templates: new TaxDocumentTemplateOptions { TemplateVersion = "2" });
        var issued = await before.IssuedAsync(snapshot: snapshot);
        await before.Service.RenderCopyAsync(issued.Id, Flow.Seller);
        var first = before.Pdf.Htmls[^1];

        // The adopter later switches new documents to version 3; the stored document still says "2".
        var after = new Flow(templates: new TaxDocumentTemplateOptions { TemplateVersion = "3" });
        var fresh = await after.IssuedAsync(snapshot: snapshot);
        Assert.Equal("3", fresh.TemplateVersion);
        after.InMemory.Replace(fresh.Id, _ => issued with { Id = fresh.Id });
        await after.Service.RenderCopyAsync(fresh.Id, Flow.Seller);

        Assert.Contains("หนึ่งร้อยเอ็ดบาทถ้วน", System.Net.WebUtility.HtmlDecode(first));
        Assert.Equal(first, after.Pdf.Htmls[^1]);
    }

    [Fact]
    public async Task Service_ShouldStillRenderADocumentIssuedUnderVersion1_AfterTheAdopterOptsIntoVersion2()
    {
        var flow = new Flow(templates: new TaxDocumentTemplateOptions { TemplateVersion = "2" });
        var issued = await flow.IssuedAsync();
        flow.InMemory.Replace(issued.Id, r => r with { TemplateVersion = "1" });

        var render = await flow.Service.RenderCopyAsync(issued.Id, Flow.Seller);

        Assert.NotNull(render);
        Assert.DoesNotContain("In words", System.Net.WebUtility.HtmlDecode(flow.Pdf.Htmls[^1]));
    }

    [Fact]
    public void Options_ShouldBeValidatedWhereTheyAreUsed()
    {
        var bad = new TaxDocumentTemplateOptions { TemplateVersion = "9" };

        Assert.Throws<InvalidOperationException>(() => new Flow(templates: bad));
        Assert.Throws<ArgumentNullException>(() => new TaxDocumentService(null!, null!, null!, null!, null!, TimeProvider.System, null!));
    }

    [Fact]
    public void ConfigureThemiaETaxTemplates_ShouldShareOneOptionsInstance_InAnyOrder()
    {
        var before = new ServiceCollection();
        before.AddLogging();
        before.ConfigureThemiaETaxTemplates(o => { o.TemplateVersion = "3"; });
        before.AddThemiaETaxRendering();
        var after = new ServiceCollection();
        after.AddLogging();
        after.AddThemiaETaxRendering();
        after.ConfigureThemiaETaxTemplates(o => { o.TemplateVersion = "3"; });

        foreach (var services in new[] { before, after })
        {
            Assert.Single(services, d => d.ServiceType == typeof(TaxDocumentTemplateOptions));
            using var provider = services.BuildServiceProvider();
            var options = provider.GetRequiredService<TaxDocumentTemplateOptions>();
            Assert.Equal("3", options.TemplateVersion);
            var snapshot = Samples.Receipt() with { Totals = Samples.Receipt().Totals with { InclVatSatang = 10_100 } };
            var html = provider.GetRequiredService<TaxDocumentRenderer>().RenderHtml(snapshot, options.TemplateVersion, TaxDocumentMarker.Original);
            Assert.Contains("หนึ่งร้อยหนึ่งบาทถ้วน", System.Net.WebUtility.HtmlDecode(html));
        }
    }

    [Fact]
    public void ConfigureThemiaETaxTemplates_ShouldFailAtRegistration_WhenTheVersionIsUnknown()
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().ConfigureThemiaETaxTemplates(o => o.TemplateVersion = "9"));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().ConfigureThemiaETaxTemplates(null!));
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).ConfigureThemiaETaxTemplates(_ => { }));
    }
}
