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
        new() { Title = "Product", Value = s => s.Product, Aggregate = AggregateKind.Label },
        new() { Title = "Amount", Value = s => s.Amount, NumberFormat = "#,##0.00", Aggregate = AggregateKind.Sum },
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
            new() { Title = "Amount", Value = s => s, Aggregate = AggregateKind.Sum },
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
}
