namespace Themia.Export.Csv;

/// <summary>Writes a row collection to a CSV file (RFC 4180), with optional report-header lines and a
/// computed summary row. CSV is data-only — <see cref="ExportColumn{T}.NumberFormat"/> is not applied.</summary>
public interface ICsvExporter
{
    /// <summary>Exports <paramref name="rows"/> as CSV.</summary>
    /// <param name="rows">The data rows.</param>
    /// <param name="columns">The column descriptors (at least one).</param>
    /// <param name="headers">Optional title lines above the table.</param>
    /// <param name="fileName">Optional download name; defaults to <c>report-{timestamp}.csv</c>.</param>
    /// <returns>The produced CSV file.</returns>
    ExportResult Export<T>(
        IEnumerable<T> rows,
        IReadOnlyList<ExportColumn<T>> columns,
        IEnumerable<ReportHeader>? headers = null,
        string? fileName = null);
}
