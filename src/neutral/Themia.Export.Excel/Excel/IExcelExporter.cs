using Themia.Export;

namespace Themia.Export.Excel;

/// <summary>Writes a row collection to an <c>.xlsx</c> workbook with a themed table, optional report
/// headers, per-column number format/alignment, and a computed summary row.</summary>
public interface IExcelExporter
{
    /// <summary>Exports <paramref name="rows"/> as an Excel workbook.</summary>
    /// <param name="rows">The data rows.</param>
    /// <param name="columns">The column descriptors (at least one).</param>
    /// <param name="options">Workbook options; <see langword="null"/> uses defaults.</param>
    /// <param name="headers">Optional title lines above the table.</param>
    /// <param name="fileName">Optional download name; defaults to <c>report-{timestamp}.xlsx</c>.</param>
    /// <returns>The produced workbook.</returns>
    /// <exception cref="InvalidOperationException">A non-numeric value appears in an aggregated column.</exception>
    ExportResult Export<T>(
        IEnumerable<T> rows,
        IReadOnlyList<ExportColumn<T>> columns,
        ExcelExportOptions? options = null,
        IEnumerable<ReportHeader>? headers = null,
        string? fileName = null);
}
