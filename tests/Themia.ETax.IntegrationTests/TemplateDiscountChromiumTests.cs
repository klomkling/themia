using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Themia.ETax.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class TemplateDiscountChromiumTests
{
    private static int Pages(byte[] pdf) => Regex.Matches(System.Text.Encoding.Latin1.GetString(pdf), @"/Type\s*/Page[^s]").Count;

    // The fullest single page: every line the layout allows, a discount (three rows replace the subtotal row), withholding (two more rows).
    private static TaxDocumentSnapshot DiscountedReceipt(int lines, long withheld)
    {
        var receipt = TemplateVersion4ChromiumTests.ReceiptFor(lines, 0);
        var purchase = receipt.Lines.Sum(l => l.AmountExVatSatang);
        var discount = purchase / 4;
        var net = VatMath.AddVat(purchase - discount, 700);
        return receipt with
        {
            Discount = new TaxDocumentDiscount { Description = "ส่วนลดจากเครดิต", AmountExVatSatang = discount },
            Totals = new TaxDocumentTotals
            {
                ExVatSatang = net.ExVatSatang,
                VatSatang = net.VatSatang,
                InclVatSatang = net.InclVatSatang,
                WithheldSatang = withheld,
                NetReceivedSatang = net.InclVatSatang - withheld,
                VatRateBasisPoints = 700,
            },
        };
    }

    [Theory]
    [InlineData("4")]
    [InlineData("5")]
    [InlineData("6")]
    [InlineData("7")]
    public async Task Render_ShouldKeepAFullDiscountedReceiptWithWithholdingOnOnePage(string version)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThemiaETaxRendering(o => ChromiumPath.Apply(o));
        await using var host = services.BuildServiceProvider();
        var size = TaxDocumentTemplates.PageSizeOf(version)!.Value;
        var single = TaxDocumentPageLayout.SinglePageRows(size, TaxDocumentKind.TaxInvoiceReceipt);

        var render = await host.GetRequiredService<TaxDocumentRenderer>()
            .RenderAsync(DiscountedReceipt(single, withheld: 3_000), version, TaxDocumentMarker.Original);

        Assert.Equal(1, Pages(render.Pdf));
    }

    // A discounted receipt whose last page holds exactly the layout's last-page budget: the two extra totals rows (discount and remaining) must not push
    // the payment or signature block onto another page, or "page x of y" is wrong on a tax document.
    public static TheoryData<string, bool> LastPageCases()
    {
        var data = new TheoryData<string, bool>();
        foreach (var version in new[] { "4", "5", "6", "7" })
        {
            data.Add(version, false);
            data.Add(version, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(LastPageCases))]
    public async Task Render_ShouldKeepTheLastPageOfADiscountedReceiptWithWithholdingWithinThePlannedPages(string version, bool withMiddlePage)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThemiaETaxRendering(o => ChromiumPath.Apply(o));
        await using var host = services.BuildServiceProvider();
        var size = TaxDocumentTemplates.PageSizeOf(version)!.Value;
        var kind = TaxDocumentKind.TaxInvoiceReceipt;
        var lines = TaxDocumentPageLayout.SinglePageRows(size, kind) + TaxDocumentPageLayout.LastPageRows(size, kind)
            + (withMiddlePage ? TaxDocumentPageLayout.MiddlePageRows(size, kind) : 0);
        var planned = TaxDocumentPageLayout.Plan(size, kind, lines).Count;

        var render = await host.GetRequiredService<TaxDocumentRenderer>()
            .RenderAsync(DiscountedReceipt(lines, withheld: 3_000), version, TaxDocumentMarker.Original);

        Assert.Equal(withMiddlePage ? 3 : 2, planned);
        Assert.Equal(planned, Pages(render.Pdf));
    }
}
