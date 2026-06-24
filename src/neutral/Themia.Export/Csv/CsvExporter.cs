using System.Globalization;
using System.Text;
using Themia.Export.Internal;

namespace Themia.Export.Csv;

/// <summary>Default <see cref="ICsvExporter"/>. Stateless and thread-safe.</summary>
public sealed class CsvExporter : ICsvExporter
{
    private const string CsvContentType = "text/csv";

    /// <inheritdoc />
    public ExportResult Export<T>(
        IEnumerable<T> rows,
        IReadOnlyList<ExportColumn<T>> columns,
        IEnumerable<ReportHeader>? headers = null,
        string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0)
        {
            throw new ArgumentException("At least one column is required.", nameof(columns));
        }

        var colCount = columns.Count;
        var matrix = RowProjector.Project(rows, columns);
        var sb = new StringBuilder();

        if (headers is not null)
        {
            foreach (var header in headers)
            {
                // Pad to the column count so every line has the same field count (rectangular CSV).
                var fields = new string[colCount];
                fields[0] = header.Line;
                for (var i = 1; i < colCount; i++)
                {
                    fields[i] = string.Empty;
                }

                AppendLine(sb, fields.Select(Quote));
            }
        }

        AppendLine(sb, columns.Select(c => Quote(c.Title)));

        foreach (var cells in matrix)
        {
            AppendLine(sb, cells.Select(v => Quote(Render(v))));
        }

        if (columns.Any(c => c.Aggregate != AggregateKind.None))
        {
            var summary = new string[colCount];
            for (var c = 0; c < colCount; c++)
            {
                var value = AggregateComputer.Compute(columns[c].Aggregate, columns[c].Title, Column(matrix, c));
                summary[c] = Quote(Render(value));
            }

            AppendLine(sb, summary);
        }

        // UTF-8 BOM so Excel detects the encoding (correct rendering of Thai/non-ASCII content).
        // GetBytes(string) never emits the BOM — prepend the preamble explicitly.
        var enc = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        var preamble = enc.GetPreamble();
        var content = enc.GetBytes(sb.ToString());
        var bytes = new byte[preamble.Length + content.Length];
        preamble.CopyTo(bytes, 0);
        content.CopyTo(bytes, preamble.Length);
        return new ExportResult(bytes, CsvContentType, fileName ?? DefaultFileName());
    }

    private static IEnumerable<object?> Column(object?[][] matrix, int c)
    {
        foreach (var row in matrix)
        {
            yield return row[c];
        }
    }

    private static string Render(object? value) => value switch
    {
        null => string.Empty,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string Quote(string field)
    {
        if (field.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return field;
        }

        return "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static void AppendLine(StringBuilder sb, IEnumerable<string> fields)
    {
        sb.Append(string.Join(',', fields)).Append("\r\n");
    }

    private static string DefaultFileName() =>
        "report-" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ".csv";
}
