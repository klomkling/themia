using Xunit;

namespace Themia.ETax.Tests;

public sealed class A5PaginationTests
{
    private static IReadOnlyList<A5Page> Plan(TaxDocumentKind kind, int lines) => A5Layout.Plan(kind, lines);

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    public void Plan_ShouldKeepAReceiptOnOnePage_UpToTheSinglePageCapacity(int lines)
    {
        var page = Assert.Single(Plan(TaxDocumentKind.TaxInvoiceReceipt, lines));

        Assert.Equal(A5PageRole.Single, page.Role);
        Assert.Equal(0, page.Start);
        Assert.Equal(lines, page.Count);
    }

    [Fact]
    public void Plan_ShouldKeepACreditNoteOnOnePage_UpToItsSmallerCapacity()
    {
        Assert.Single(Plan(TaxDocumentKind.CreditNote, 4));
        Assert.Equal(2, Plan(TaxDocumentKind.CreditNote, 5).Count);
    }

    [Fact]
    public void Plan_ShouldContinueOnASecondPage_WhenTheLinesDoNotFit()
    {
        var pages = Plan(TaxDocumentKind.TaxInvoiceReceipt, 7);

        Assert.Equal([A5PageRole.First, A5PageRole.Last], pages.Select(p => p.Role));
        Assert.Equal([(0, 6), (6, 1)], pages.Select(p => (p.Start, p.Count)));
    }

    [Fact]
    public void Plan_ShouldFillTheLastPageToItsCapacity_BeforeAddingAnother()
    {
        var capacity = A5Layout.LastPageRows(TaxDocumentKind.TaxInvoiceReceipt);

        Assert.Equal(2, Plan(TaxDocumentKind.TaxInvoiceReceipt, 6 + capacity).Count);
        Assert.Equal(3, Plan(TaxDocumentKind.TaxInvoiceReceipt, 6 + capacity + 1).Count);
    }

    [Fact]
    public void Plan_ShouldRepeatTheCompactHeaderOnAMiddlePage()
    {
        var middle = A5Layout.MiddlePageRows(TaxDocumentKind.TaxInvoiceReceipt);
        var capacity = A5Layout.LastPageRows(TaxDocumentKind.TaxInvoiceReceipt);

        var pages = Plan(TaxDocumentKind.TaxInvoiceReceipt, 6 + middle + capacity);

        Assert.Equal([A5PageRole.First, A5PageRole.Middle, A5PageRole.Last], pages.Select(p => p.Role));
        Assert.Equal(6 + middle + capacity, pages.Sum(p => p.Count));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(20)]
    [InlineData(45)]
    public void Plan_ShouldNeverLoseOrRepeatALine_AndKeepTheLinesInOrder(int lines)
    {
        var pages = Plan(TaxDocumentKind.TaxInvoiceReceipt, lines);

        var next = 0;
        foreach (var page in pages)
        {
            Assert.Equal(next, page.Start);
            Assert.True(page.Count >= 1);
            next += page.Count;
        }

        Assert.Equal(lines, next);
    }

    [Fact]
    public void Plan_ShouldRespectEveryPagesCapacity_ForEveryLineCountUpToAHundred()
    {
        foreach (var kind in new[] { TaxDocumentKind.TaxInvoiceReceipt, TaxDocumentKind.CreditNote })
        {
            for (var lines = 1; lines <= 100; lines++)
            {
                var pages = Plan(kind, lines);

                Assert.Equal(lines, pages.Sum(p => p.Count));
                Assert.All(pages, p => Assert.True(p.Count >= 1, $"{kind} {lines}: an empty page"));
                Assert.Equal(pages.Count == 1 ? A5PageRole.Single : A5PageRole.First, pages[0].Role);
                Assert.All(pages.Where(p => p.Role == A5PageRole.Middle), p => Assert.True(p.Count <= A5Layout.MiddlePageRows(kind)));
                if (pages.Count > 1)
                {
                    Assert.Equal(A5PageRole.Last, pages[^1].Role);
                    Assert.True(pages[^1].Count <= A5Layout.LastPageRows(kind), $"{kind} {lines}: last page over capacity");
                    Assert.Equal(A5Layout.SinglePageRows(kind), pages[0].Count);
                }
            }
        }
    }

    [Fact]
    public void Plan_ShouldRejectNoLines() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Plan(TaxDocumentKind.TaxInvoiceReceipt, 0));
}
