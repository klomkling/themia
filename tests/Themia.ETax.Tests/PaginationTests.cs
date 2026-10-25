using Xunit;

namespace Themia.ETax.Tests;

public sealed class PaginationTests
{
    private const TaxDocumentPageSize A4 = TaxDocumentPageSize.A4Portrait;
    private const TaxDocumentPageSize A5 = TaxDocumentPageSize.A5Landscape;

    private static TaxDocumentPageSize SizeOf(bool a4) => a4 ? A4 : A5;

    private static IReadOnlyList<TaxDocumentPagePlan> Plan(TaxDocumentPageSize size, TaxDocumentKind kind, int lines) =>
        TaxDocumentPageLayout.Plan(size, kind, lines);

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 3)]
    [InlineData(false, 6)]
    [InlineData(true, 1)]
    [InlineData(true, 12)]
    [InlineData(true, 32)]
    public void Plan_ShouldKeepAReceiptOnOnePage_UpToTheSinglePageCapacity(bool a4, int lines)
    {
        var size = SizeOf(a4);
        var page = Assert.Single(Plan(size, TaxDocumentKind.TaxInvoiceReceipt, lines));

        Assert.Equal(TaxDocumentPageRole.Single, page.Role);
        Assert.Equal(0, page.Start);
        Assert.Equal(lines, page.Count);
    }

    [Fact]
    public void Plan_ShouldKeepACreditNoteOnOnePage_UpToItsSmallerCapacity()
    {
        Assert.Single(Plan(A5, TaxDocumentKind.CreditNote, 4));
        Assert.Equal(2, Plan(A5, TaxDocumentKind.CreditNote, 5).Count);
        Assert.Single(Plan(A4, TaxDocumentKind.CreditNote, 30));
        Assert.Equal(2, Plan(A4, TaxDocumentKind.CreditNote, 31).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Plan_ShouldContinueOnASecondPage_OnlyWhenTheFirstIsFull(bool a4)
    {
        var size = SizeOf(a4);
        var single = TaxDocumentPageLayout.SinglePageRows(size, TaxDocumentKind.TaxInvoiceReceipt);

        var pages = Plan(size, TaxDocumentKind.TaxInvoiceReceipt, single + 1);

        Assert.Equal([TaxDocumentPageRole.First, TaxDocumentPageRole.Last], pages.Select(p => p.Role));
        Assert.Equal([(0, single), (single, 1)], pages.Select(p => (p.Start, p.Count)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Plan_ShouldFillTheLastPageToItsCapacity_BeforeAddingAnother(bool a4)
    {
        var size = SizeOf(a4);
        var single = TaxDocumentPageLayout.SinglePageRows(size, TaxDocumentKind.TaxInvoiceReceipt);
        var last = TaxDocumentPageLayout.LastPageRows(size, TaxDocumentKind.TaxInvoiceReceipt);

        Assert.Equal(2, Plan(size, TaxDocumentKind.TaxInvoiceReceipt, single + last).Count);
        Assert.Equal(3, Plan(size, TaxDocumentKind.TaxInvoiceReceipt, single + last + 1).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Plan_ShouldRepeatTheCompactHeaderOnAMiddlePage(bool a4)
    {
        var size = SizeOf(a4);
        var single = TaxDocumentPageLayout.SinglePageRows(size, TaxDocumentKind.TaxInvoiceReceipt);
        var middle = TaxDocumentPageLayout.MiddlePageRows(size, TaxDocumentKind.TaxInvoiceReceipt);
        var last = TaxDocumentPageLayout.LastPageRows(size, TaxDocumentKind.TaxInvoiceReceipt);

        var pages = Plan(size, TaxDocumentKind.TaxInvoiceReceipt, single + middle + last);

        Assert.Equal([TaxDocumentPageRole.First, TaxDocumentPageRole.Middle, TaxDocumentPageRole.Last], pages.Select(p => p.Role));
        Assert.Equal(single + middle + last, pages.Sum(p => p.Count));
    }

    [Fact]
    public void Plan_ShouldRespectEveryPagesCapacity_ForEveryLineCountUpToTwoHundred()
    {
        foreach (var size in new[] { A5, A4 })
        {
            foreach (var kind in new[] { TaxDocumentKind.TaxInvoiceReceipt, TaxDocumentKind.CreditNote })
            {
                for (var lines = 1; lines <= 200; lines++)
                {
                    var pages = Plan(size, kind, lines);

                    Assert.Equal(lines, pages.Sum(p => p.Count));
                    Assert.All(pages, p => Assert.True(p.Count >= 1, $"{size} {kind} {lines}: an empty page"));
                    Assert.Equal(pages.Count == 1 ? TaxDocumentPageRole.Single : TaxDocumentPageRole.First, pages[0].Role);
                    Assert.All(pages.Where(p => p.Role == TaxDocumentPageRole.Middle), p => Assert.True(p.Count <= TaxDocumentPageLayout.MiddlePageRows(size, kind)));
                    if (pages.Count > 1)
                    {
                        Assert.Equal(TaxDocumentPageRole.Last, pages[^1].Role);
                        Assert.True(pages[^1].Count <= TaxDocumentPageLayout.LastPageRows(size, kind), $"{size} {kind} {lines}: last page over capacity");
                        Assert.Equal(TaxDocumentPageLayout.SinglePageRows(size, kind), pages[0].Count);
                    }
                }
            }
        }
    }

    [Fact]
    public void PaddedRows_ShouldPadASmallFormToItsFullHeight_AndLeaveA4Unpadded()
    {
        Assert.Equal(6, TaxDocumentPageLayout.PaddedRows(A5, TaxDocumentKind.TaxInvoiceReceipt));
        Assert.Equal(4, TaxDocumentPageLayout.PaddedRows(A5, TaxDocumentKind.CreditNote));
        Assert.Equal(0, TaxDocumentPageLayout.PaddedRows(A4, TaxDocumentKind.TaxInvoiceReceipt));
    }

    [Fact]
    public void Plan_ShouldRejectNoLines() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Plan(A4, TaxDocumentKind.TaxInvoiceReceipt, 0));
}
