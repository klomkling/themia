namespace Themia.ETax;

/// <summary>The paper a paged template (versions "4" and "5") prints on.</summary>
internal enum TaxDocumentPageSize
{
    /// <summary>A4 portrait, 210 x 297 mm (version "4"): the default page, because our documents are PDFs. Content flows from the top and is not padded.</summary>
    A4Portrait,

    /// <summary>A5 landscape, 210 x 148 mm (version "5"): a small form; a single page keeps its table at the form's full height.</summary>
    A5Landscape,
}

/// <summary>Which part of a document a page carries.</summary>
internal enum TaxDocumentPageRole
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

/// <summary>The lines of one page: <see cref="Count"/> lines from <see cref="Start"/>.</summary>
internal readonly record struct TaxDocumentPagePlan(TaxDocumentPageRole Role, int Start, int Count);

/// <summary>
/// How many lines fit a page, and how a document's lines are split over pages. The counts are fixed constants, never measured at render time and never
/// shrunk to fit (that would make the same document print differently on two hosts); a Chromium test per size and kind proves each count still fits one
/// page with the embedded Sarabun at the template's sizes. A document continues on a further page only when the page is full.
/// </summary>
internal static class TaxDocumentPageLayout
{
    // A receipt page with the full header, the buyer box, and the totals, payment and signature blocks. A credit note spends the room of the reason block.
    // A5 is the designer's page budget (six rows; a credit note four). A4 was measured on Chromium (a receipt with withholding rows fits 35 lines, a credit
    // note 33; a middle page 42; the last page 37 and 35) and set three to four rows lower, so a two-line address or a wrapped description still fits.
    public static int SinglePageRows(TaxDocumentPageSize size, TaxDocumentKind kind) => (size, kind == TaxDocumentKind.TaxInvoiceReceipt) switch
    {
        (TaxDocumentPageSize.A4Portrait, true) => 32,
        (TaxDocumentPageSize.A4Portrait, false) => 30,
        (_, true) => 6,
        _ => 4,
    };

    // A middle page: compact header, no buyer box, no closing blocks.
    public static int MiddlePageRows(TaxDocumentPageSize size, TaxDocumentKind kind) => size == TaxDocumentPageSize.A4Portrait ? 38 : 12;

    // The last page: compact header (no buyer box) and all the closing blocks.
    public static int LastPageRows(TaxDocumentPageSize size, TaxDocumentKind kind) => (size, kind == TaxDocumentKind.TaxInvoiceReceipt) switch
    {
        (TaxDocumentPageSize.A4Portrait, true) => 34,
        (TaxDocumentPageSize.A4Portrait, false) => 32,
        (_, true) => 9,
        _ => 5,
    };

    /// <summary>
    /// Rows a single page's table is padded to with empty rows, so a small form keeps its shape. None on A4: the document flows from the top and
    /// ends where its content ends.
    /// </summary>
    public static int PaddedRows(TaxDocumentPageSize size, TaxDocumentKind kind) =>
        size == TaxDocumentPageSize.A5Landscape ? SinglePageRows(size, kind) : 0;

    /// <summary>Splits <paramref name="lineCount"/> lines over pages; every page carries at least one line.</summary>
    public static IReadOnlyList<TaxDocumentPagePlan> Plan(TaxDocumentPageSize size, TaxDocumentKind kind, int lineCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(lineCount, 1);

        var single = SinglePageRows(size, kind);
        if (lineCount <= single)
            return [new TaxDocumentPagePlan(TaxDocumentPageRole.Single, 0, lineCount)];

        var pages = new List<TaxDocumentPagePlan> { new(TaxDocumentPageRole.First, 0, single) };
        var next = single;
        while (lineCount - next > LastPageRows(size, kind))
        {
            // Never take the line the last page needs: a document of n lines always ends on a page with at least one.
            var take = Math.Min(MiddlePageRows(size, kind), lineCount - next - 1);
            pages.Add(new TaxDocumentPagePlan(TaxDocumentPageRole.Middle, next, take));
            next += take;
        }

        pages.Add(new TaxDocumentPagePlan(TaxDocumentPageRole.Last, next, lineCount - next));
        return pages;
    }
}
