using System.Text;
using Themia.Export;
using Themia.Export.Csv;
using Xunit;

namespace Themia.Export.Tests;

public sealed class CsvExporterTests
{
    private sealed record Sale(string Product, decimal Amount);

    private static readonly ExportColumn<Sale>[] Columns =
    [
        new() { Title = "Product", Selector = s => s.Product, Aggregate = AggregateKind.Label },
        new() { Title = "Amount", Selector = s => s.Amount, Aggregate = AggregateKind.Sum },
    ];

    private static string Text(ExportResult r) =>
        new UTF8Encoding(false).GetString(r.Content.AsSpan(GetBomLength(r.Content)));

    private static int GetBomLength(byte[] b) =>
        b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0;

    [Fact]
    public void Writes_header_data_and_summary_rows()
    {
        var rows = new[] { new Sale("Apple", 10m), new Sale("Pear", 5m) };

        var result = new CsvExporter().Export(rows, Columns);

        Assert.Equal("text/csv", result.ContentType);
        var lines = Text(result).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Product,Amount", lines[0]);
        Assert.Equal("Apple,10", lines[1]);
        Assert.Equal("Pear,5", lines[2]);
        Assert.Equal("Product,15", lines[3]); // Label echoes the title; Sum = 15
    }

    [Fact]
    public void Quotes_fields_with_comma_quote_or_newline()
    {
        var cols = new ExportColumn<string>[] { new() { Title = "V", Selector = s => s } };
        var rows = new[] { "a,b", "he said \"hi\"", "line1\nline2" };

        var lines = Text(new CsvExporter().Export(rows, cols)).Split("\r\n");

        Assert.Equal("\"a,b\"", lines[1]);
        Assert.Equal("\"he said \"\"hi\"\"\"", lines[2]);
        Assert.Equal("\"line1\nline2\"", lines[3]);
    }

    [Fact]
    public void Quotes_field_containing_bare_carriage_return()
    {
        // The Quote helper triggers on '\r' as well as '\n' and ','. Lock that behaviour.
        var cols = new ExportColumn<string>[] { new() { Title = "V", Selector = s => s } };
        var rows = new[] { "line1\rline2" };

        var lines = Text(new CsvExporter().Export(rows, cols)).Split("\r\n");

        Assert.Equal("\"line1\rline2\"", lines[1]);
    }

    [Fact]
    public void Report_headers_are_padded_to_column_count()
    {
        var rows = new[] { new Sale("Apple", 10m) };
        var headers = new ReportHeader[] { new("My Report") };

        var first = Text(new CsvExporter().Export(rows, Columns, headers)).Split("\r\n")[0];

        Assert.Equal("My Report,", first); // 2 columns => one trailing empty field
    }

    [Fact]
    public void Empty_rows_still_emit_title_and_summary()
    {
        var lines = Text(new CsvExporter().Export(Array.Empty<Sale>(), Columns)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Product,Amount", lines[0]);
        Assert.Equal("Product,", lines[1]); // Label title; Sum over no rows => blank
    }

    [Fact]
    public void Starts_with_utf8_bom_for_excel_thai_support()
    {
        var bytes = new CsvExporter().Export(new[] { new Sale("กล้วย", 1m) }, Columns).Content;
        Assert.True(bytes is [0xEF, 0xBB, 0xBF, ..]);
    }

    // Item H: All AggregateKind.None => no summary row emitted.
    [Fact]
    public void No_summary_row_when_all_aggregates_are_none()
    {
        var cols = new ExportColumn<Sale>[]
        {
            new() { Title = "Product", Selector = s => s.Product },
            new() { Title = "Amount", Selector = s => s.Amount },
        };
        var rows = new[] { new Sale("Apple", 10m) };

        var lines = Text(new CsvExporter().Export(rows, cols)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        // Expect only: header + one data row (no summary).
        Assert.Equal(2, lines.Length);
        Assert.Equal("Product,Amount", lines[0]);
        Assert.Equal("Apple,10", lines[1]);
    }
}
