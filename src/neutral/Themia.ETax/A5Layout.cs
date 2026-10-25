namespace Themia.ETax;

/// <summary>Which part of a document an A5 page carries.</summary>
internal enum A5PageRole
{
    /// <summary>The whole document: full header, every line, totals, payment and signature.</summary>
    Single,

    /// <summary>The first of several: full header and lines, then the carried-forward subtotal. No totals.</summary>
    First,

    /// <summary>A page between the first and the last: compact header, brought-forward row, lines, carried-forward row.</summary>
    Middle,

    /// <summary>The last of several: compact header, brought-forward row, the remaining lines, then totals, payment and signature.</summary>
    Last,
}

/// <summary>The lines of one A5 page: <see cref="Count"/> lines from <see cref="Start"/>.</summary>
internal readonly record struct A5Page(A5PageRole Role, int Start, int Count);

/// <summary>
/// How many lines fit an A5 landscape page, and how a document's lines are split over pages. The counts are fixed constants, never measured at render
/// time and never shrunk to fit (that would make the same document print differently on two hosts); a render test per kind proves each count still
/// fits one page with the embedded Sarabun at the template's sizes.
/// </summary>
internal static class A5Layout
{
    // A receipt page holds six rows with the full header and the totals, payment and signature blocks (the designer's page budget). A credit note
    // spends the room of two rows on the reason block, so it holds four.
    private const int ReceiptSinglePageRows = 6;
    private const int CreditNoteSinglePageRows = 4;

    // Page 1 of several has no totals block, but it keeps the full header and the buyer box, so it holds the same count as a single page.
    private const int ReceiptMiddlePageRows = 12;
    private const int CreditNoteMiddlePageRows = 12;

    // The last page has a compact header (no buyer box) but all the closing blocks.
    private const int ReceiptLastPageRows = 9;
    private const int CreditNoteLastPageRows = 5;

    public static int SinglePageRows(TaxDocumentKind kind) => kind == TaxDocumentKind.TaxInvoiceReceipt ? ReceiptSinglePageRows : CreditNoteSinglePageRows;

    public static int MiddlePageRows(TaxDocumentKind kind) => kind == TaxDocumentKind.TaxInvoiceReceipt ? ReceiptMiddlePageRows : CreditNoteMiddlePageRows;

    public static int LastPageRows(TaxDocumentKind kind) => kind == TaxDocumentKind.TaxInvoiceReceipt ? ReceiptLastPageRows : CreditNoteLastPageRows;

    /// <summary>Splits <paramref name="lineCount"/> lines over pages; every page carries at least one line.</summary>
    public static IReadOnlyList<A5Page> Plan(TaxDocumentKind kind, int lineCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(lineCount, 1);

        var single = SinglePageRows(kind);
        if (lineCount <= single)
            return [new A5Page(A5PageRole.Single, 0, lineCount)];

        var pages = new List<A5Page> { new(A5PageRole.First, 0, single) };
        var next = single;
        while (lineCount - next > LastPageRows(kind))
        {
            // Never take the line the last page needs: a document of n lines always ends on a page with at least one.
            var take = Math.Min(MiddlePageRows(kind), lineCount - next - 1);
            pages.Add(new A5Page(A5PageRole.Middle, next, take));
            next += take;
        }

        pages.Add(new A5Page(A5PageRole.Last, next, lineCount - next));
        return pages;
    }
}
