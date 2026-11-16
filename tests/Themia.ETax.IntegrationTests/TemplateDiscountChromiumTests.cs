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
}
