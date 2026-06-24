using Themia.Export;

namespace Themia.Export.Excel;

/// <summary>Writes a row collection to an <c>.xlsx</c> workbook with a themed table, optional report
/// headers, per-column number format/alignment, and a computed summary row.</summary>
public interface IExcelExporter
{
    /// <summary>Exports <paramref name="rows"/> as an Excel workbook.</summary>
    /// <remarks>
    /// Cell values are written via ClosedXML. Natively-supported types are <see cref="string"/>,
    /// <see cref="bool"/>, the numeric types (<see cref="int"/>, <see cref="long"/>,
    /// <see cref="double"/>, <see cref="decimal"/>, etc.), <see cref="DateTime"/>, and
    /// <see cref="TimeSpan"/>. Other types (e.g. <see cref="DateTimeOffset"/>, <see cref="Guid"/>,
    /// custom objects) are written as their invariant string form — callers should convert to a
    /// supported type for typed cells.
    /// </remarks>
    /// <param name="rows">The data rows.</param>
    /// <param name="columns">The column descriptors (at least one).</param>
    /// <param name="options">Workbook options; <see langword="null"/> uses defaults.</param>
    /// <param name="headers">Optional title lines above the table.</param>
    /// <param name="fileName">Optional download name; defaults to <c>report-{timestamp}.xlsx</c>.
    /// Pass an explicit name when guaranteed uniqueness is required (the default includes millisecond
    /// precision but is not collision-proof under concurrent calls).</param>
    /// <returns>The produced .xlsx file as an <see cref="Themia.Export.ExportResult"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rows"/> or <paramref name="columns"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="columns"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">A non-numeric value appears in an aggregated column.</exception>
    ExportResult Export<T>(
        IEnumerable<T> rows,
        IReadOnlyList<ExportColumn<T>> columns,
        ExcelExportOptions? options = null,
        IEnumerable<ReportHeader>? headers = null,
        string? fileName = null);
}
