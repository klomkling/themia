using System.Globalization;
using ClosedXML.Excel;
using Themia.Export;
using Themia.Export.Internal;

namespace Themia.Export.Excel;

/// <summary>Default <see cref="IExcelExporter"/>. Stateless and thread-safe — a fresh
/// <see cref="XLWorkbook"/> per call. Styles by range/column (never per cell) and sizes columns
/// without full-sheet auto-fit.</summary>
public sealed class ExcelExporter : IExcelExporter
{
    private const string XlsxContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <inheritdoc />
    public ExportResult Export<T>(
        IEnumerable<T> rows,
        IReadOnlyList<ExportColumn<T>> columns,
        ExcelExportOptions? options = null,
        IEnumerable<ReportHeader>? headers = null,
        string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0)
        {
            throw new ArgumentException("At least one column is required.", nameof(columns));
        }

        options ??= new ExcelExportOptions();
        var colCount = columns.Count;

        // Detect duplicate titles before building the workbook — ClosedXML.CreateTable() requires
        // unique header names and throws an opaque exception otherwise.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in columns)
        {
            if (!seen.Add(col.Title))
            {
                throw new ArgumentException(
                    $"Duplicate column title '{col.Title}'. Excel table column titles must be unique.",
                    nameof(columns));
            }
        }

        var matrix = RowProjector.Project(rows, columns);
        var headerList = headers?.ToList() ?? [];

        using var workbook = new XLWorkbook();
        workbook.Style.Font.FontName = options.FontName;
        var ws = workbook.Worksheets.Add(options.SheetName);

        // 1) Report header lines (merged across the columns).
        for (var h = 0; h < headerList.Count; h++)
        {
            ws.Cell(h + 1, 1).Value = headerList[h].Line;
            ws.Range(h + 1, 1, h + 1, colCount).Merge();
        }

        var titleRow = headerList.Count + 1;
        var firstDataRow = titleRow + 1;
        var rowCount = matrix.Length;
        var lastDataRow = firstDataRow + rowCount - 1; // == titleRow when rowCount == 0

        // 2) Title row (value + alignment).
        for (var c = 0; c < colCount; c++)
        {
            ws.Cell(titleRow, c + 1).Value = columns[c].Title;
            var a = Map(columns[c].Alignment);
            if (a is { } al)
            {
                ws.Cell(titleRow, c + 1).Style.Alignment.Horizontal = al;
            }
        }

        // 3) Bulk-insert the data matrix in one call (no per-cell loop).
        if (rowCount > 0)
        {
            ws.Cell(firstDataRow, 1).InsertData(matrix);
        }

        // 4) Themed table over the title + data block (header only when there are no data rows).
        var tableRange = ws.Range(titleRow, 1, Math.Max(titleRow, lastDataRow), colCount);
        var table = tableRange.CreateTable();
        table.Theme = options.TableTheme ?? XLTableTheme.TableStyleMedium2;

        // 5) Per-column number format + alignment, applied once to the data range (O(columns)).
        if (rowCount > 0)
        {
            for (var c = 0; c < colCount; c++)
            {
                var range = ws.Range(firstDataRow, c + 1, lastDataRow, c + 1).Style;
                if (!string.IsNullOrEmpty(columns[c].NumberFormat))
                {
                    range.NumberFormat.Format = columns[c].NumberFormat;
                }

                var alignment = Map(columns[c].Alignment);
                if (alignment is { } a)
                {
                    range.Alignment.Horizontal = a;
                }
            }
        }

        // 6) Summary row (literals from the shared engine), directly below the table.
        if (columns.Any(c => c.Aggregate != AggregateKind.None))
        {
            var summaryRow = Math.Max(titleRow, lastDataRow) + 1;
            for (var c = 0; c < colCount; c++)
            {
                var value = AggregateComputer.Compute(columns[c].Aggregate, columns[c].Title, RowProjector.Column(matrix, c));
                var cell = ws.Cell(summaryRow, c + 1);
                switch (value)
                {
                    case decimal d:
                        cell.Value = d;
                        if (!string.IsNullOrEmpty(columns[c].NumberFormat))
                        {
                            cell.Style.NumberFormat.Format = columns[c].NumberFormat;
                        }

                        break;
                    case string s:
                        cell.Value = s;
                        break;
                }

                var a = Map(columns[c].Alignment);
                if (a is { } al)
                {
                    cell.Style.Alignment.Horizontal = al;
                }
            }

            var summary = ws.Range(summaryRow, 1, summaryRow, colCount).Style;
            summary.Font.Bold = true;
            summary.Border.TopBorder = XLBorderStyleValues.Thin;
        }

        // 7) Column widths.
        ApplyWidths(ws, columns, matrix, titleRow, firstDataRow, options);

        if (options.FreezeHeaderRow)
        {
            ws.SheetView.FreezeRows(titleRow);
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return new ExportResult(stream.ToArray(), XlsxContentType, fileName ?? DefaultFileName());
    }

    private static XLAlignmentHorizontalValues? Map(ColumnAlignment alignment) => alignment switch
    {
        ColumnAlignment.Left => XLAlignmentHorizontalValues.Left,
        ColumnAlignment.Center => XLAlignmentHorizontalValues.Center,
        ColumnAlignment.Right => XLAlignmentHorizontalValues.Right,
        _ => null, // Auto: leave ClosedXML's type-based default.
    };

    private static void ApplyWidths<T>(
        IXLWorksheet ws,
        IReadOnlyList<ExportColumn<T>> columns,
        object?[][] matrix,
        int titleRow,
        int firstDataRow,
        ExcelExportOptions options)
    {
        const double charFactor = 1.1;
        const double minWidth = 8;
        const double maxWidth = 80;
        var sample = Math.Min(options.WidthSampleRows, matrix.Length);

        for (var c = 0; c < columns.Count; c++)
        {
            var column = ws.Column(c + 1);
            if (columns[c].Width is { } explicitWidth)
            {
                column.Width = explicitWidth;
                continue;
            }

            switch (options.WidthMode)
            {
                case ColumnWidthMode.None:
                    break;

                case ColumnWidthMode.Measure:
                    // Always measure from the title row; when there are no data rows the title
                    // is the only row — measuring just the title is still correct.
                    column.AdjustToContents(titleRow, sample > 0 ? firstDataRow + sample - 1 : titleRow);
                    break;

                case ColumnWidthMode.Estimate:
                    var maxLen = columns[c].Title.Length;
                    for (var r = 0; r < sample; r++)
                    {
                        var len = CellText.Invariant(matrix[r][c]).Length;
                        if (len > maxLen)
                        {
                            maxLen = len;
                        }
                    }

                    column.Width = Math.Clamp(maxLen * charFactor, minWidth, maxWidth);
                    break;
            }
        }
    }

    private static string DefaultFileName() =>
        "report-" + DateTime.Now.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + ".xlsx";
}
