using ClosedXML.Excel;
using Themia.Export;
using Themia.Export.Csv;
using Themia.Export.Excel;
using Xunit;

namespace Themia.Export.Excel.Tests;

public sealed class ExcelExporterTests
{
    private sealed record Sale(string Product, decimal Amount);

    private static readonly ExportColumn<Sale>[] Columns =
    [
        new() { Title = "Product", Selector = s => s.Product, Aggregate = AggregateKind.Label },
        new() { Title = "Amount", Selector = s => s.Amount, NumberFormat = "#,##0.00", Aggregate = AggregateKind.Sum },
    ];

    private static readonly Sale[] Rows = [new("Apple", 10m), new("Pear", 5m)];

    private static IXLWorksheet Open(ExportResult r)
    {
        using var ms = new MemoryStream(r.Content);
        return new XLWorkbook(ms).Worksheet(1);
    }

    [Fact]
    public void Writes_titles_data_and_summary_with_number_format()
    {
        var result = new ExcelExporter().Export(Rows, Columns);

        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", result.ContentType);
        var ws = Open(result);
        Assert.Equal("Product", ws.Cell(1, 1).GetString());
        Assert.Equal("Amount", ws.Cell(1, 2).GetString());
        Assert.Equal(10m, ws.Cell(2, 2).GetValue<decimal>());
        Assert.Equal("#,##0.00", ws.Cell(2, 2).Style.NumberFormat.Format);
        // Summary row is directly below the 2 data rows (row 4): Label echoes title, Sum = 15.
        Assert.Equal("Product", ws.Cell(4, 1).GetString());
        Assert.Equal(15m, ws.Cell(4, 2).GetValue<decimal>());
    }

    [Fact]
    public void Summary_matches_csv_numbers_exactly()
    {
        var excel = new ExcelExporter().Export(Rows, Columns);
        var ws = Open(excel);
        var excelSum = ws.Cell(4, 2).GetValue<decimal>();

        // Same input through the CSV writer must yield the same summary number.
        var csv = new CsvExporter().Export(Rows, Columns);
        var csvText = System.Text.Encoding.UTF8.GetString(csv.Content);
        Assert.Contains("Product,15", csvText);
        Assert.Equal(15m, excelSum);
    }

    [Fact]
    public void Creates_a_themed_table_and_freezes_header()
    {
        var ws = Open(new ExcelExporter().Export(Rows, Columns));
        Assert.NotEmpty(ws.Tables);
        Assert.True(ws.SheetView.SplitRow > 0); // header frozen
    }

    [Fact]
    public void Report_headers_render_above_the_table()
    {
        var ws = Open(new ExcelExporter().Export(Rows, Columns, headers: new ReportHeader[] { new("My Report") }));
        Assert.Equal("My Report", ws.Cell(1, 1).GetString());
        Assert.Equal("Product", ws.Cell(2, 1).GetString()); // table header pushed down one row
    }

    [Fact]
    public void Non_numeric_in_aggregated_column_throws()
    {
        var cols = new ExportColumn<string>[]
        {
            new() { Title = "Amount", Selector = s => s, Aggregate = AggregateKind.Sum },
        };
        Assert.Throws<InvalidOperationException>(() => new ExcelExporter().Export(new[] { "oops" }, cols));
    }

    [Fact]
    public void Large_export_completes_in_estimate_mode()
    {
        // 5000 rows, default WidthMode.Estimate => no glyph measurement, no full-sheet auto-fit.
        var rows = Enumerable.Range(0, 5000).Select(i => new Sale("P" + i, i)).ToArray();

        var ws = Open(new ExcelExporter().Export(rows, Columns));

        // Layout: title row 1, data rows 2..5001, summary row 5002.
        Assert.Equal("P4999", ws.Cell(5001, 1).GetString());
        Assert.Equal(12_497_500m, ws.Cell(5002, 2).GetValue<decimal>()); // Sum of 0..4999
    }

    [Fact]
    public void Empty_rows_writes_header_only_table_and_blank_summary()
    {
        // rowCount == 0: table is header-only; summaryRow == titleRow + 1 == 2.
        // AggregateComputer.Compute(Sum, ...) over zero values returns null => no case in the
        // switch matches => the cell is left blank. Label returns the title string.
        var ws = Open(new ExcelExporter().Export(Array.Empty<Sale>(), Columns));

        // Title row (row 1).
        Assert.Equal("Product", ws.Cell(1, 1).GetString());
        Assert.Equal("Amount", ws.Cell(1, 2).GetString());

        // A table exists (header-only range).
        Assert.NotEmpty(ws.Tables);

        // Summary row is at row 2.
        Assert.Equal("Product", ws.Cell(2, 1).GetString()); // Label => echoes title
        Assert.True(ws.Cell(2, 2).IsEmpty());                // Sum over zero rows => null => blank
    }

    [Fact]
    public void Non_sum_numeric_aggregate_flows_through_decimal_path()
    {
        // Covers the decimal branch in ExcelExporter for AggregateKind.Average.
        var cols = new ExportColumn<Sale>[]
        {
            new() { Title = "Amount", Selector = s => s.Amount, Aggregate = AggregateKind.Average },
        };
        var rows = new[] { new Sale("A", 10m), new Sale("B", 20m) };

        var ws = Open(new ExcelExporter().Export(rows, cols));

        // Layout: title row 1, data rows 2..3, summary row 4.
        Assert.Equal(15m, ws.Cell(4, 1).GetValue<decimal>()); // Average of 10 and 20
    }

    // Item C: Measure mode includes header row in width calculation.
    [Fact]
    public void Measure_mode_width_accounts_for_title_row()
    {
        // Title is much longer than the single data value, so the column must be sized
        // for the title when Measure mode is used (since it now starts from titleRow).
        var cols = new ExportColumn<Sale>[]
        {
            new() { Title = "A Very Long Column Title", Selector = s => s.Product },
        };
        var rows = new[] { new Sale("X", 1m) };

        var ws = Open(new ExcelExporter().Export(rows, cols,
            new ExcelExportOptions { WidthMode = ColumnWidthMode.Measure, WidthSampleRows = 1 }));

        // The column must be wider than a trivially narrow threshold (title is 24 chars).
        Assert.True(ws.Column(1).Width > 5, "Column should be wider than narrow data-only measure.");
    }

    // Item D: Summary cells carry column alignment.
    [Fact]
    public void Summary_cell_carries_column_alignment()
    {
        var cols = new ExportColumn<Sale>[]
        {
            new() { Title = "Amount", Selector = s => s.Amount, Aggregate = AggregateKind.Sum, Alignment = ColumnAlignment.Right },
        };
        var rows = new[] { new Sale("A", 10m), new Sale("B", 5m) };

        var ws = Open(new ExcelExporter().Export(rows, cols));

        // Layout: title=row1, data=rows2-3, summary=row4.
        var summaryAlignment = ws.Cell(4, 1).Style.Alignment.Horizontal;
        Assert.Equal(XLAlignmentHorizontalValues.Right, summaryAlignment);
    }

    // Item D: Summary cell also carries NumberFormat (existing test extended).
    [Fact]
    public void Summary_cell_carries_number_format()
    {
        // Reuse the shared Columns fixture which has NumberFormat="#,##0.00" on Amount.
        var ws = Open(new ExcelExporter().Export(Rows, Columns));
        // Summary row is row 4 (title=1, data=2-3, summary=4). Amount is column 2.
        Assert.Equal("#,##0.00", ws.Cell(4, 2).Style.NumberFormat.Format);
    }

    // Item H: FreezeHeaderRow = false => SplitRow == 0.
    [Fact]
    public void No_freeze_when_FreezeHeaderRow_is_false()
    {
        var ws = Open(new ExcelExporter().Export(Rows, Columns,
            new ExcelExportOptions { FreezeHeaderRow = false }));
        Assert.Equal(0, ws.SheetView.SplitRow);
    }

    // Item H: Explicit column Width is respected.
    [Fact]
    public void Explicit_column_width_is_applied()
    {
        var cols = new ExportColumn<Sale>[]
        {
            new() { Title = "Product", Selector = s => s.Product, Width = 30 },
        };

        var ws = Open(new ExcelExporter().Export(Rows, cols));

        Assert.Equal(30, ws.Column(1).Width);
    }

    // Item H: Two ReportHeader lines push the title to row 3.
    [Fact]
    public void Two_report_headers_push_title_to_row_three()
    {
        var headers = new ReportHeader[] { new("Header1"), new("Header2") };

        var ws = Open(new ExcelExporter().Export(Rows, Columns, headers: headers));

        Assert.Equal("Header1", ws.Cell(1, 1).GetString());
        Assert.Equal("Header2", ws.Cell(2, 1).GetString());
        Assert.Equal("Product", ws.Cell(3, 1).GetString()); // title pushed to row 3
    }
}
