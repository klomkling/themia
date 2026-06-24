namespace Themia.Export.Csv;

/// <summary>Writes a row collection to a CSV file (RFC 4180), with optional report-header lines and a
/// computed summary row. CSV is data-only — <see cref="ExportColumn{T}.NumberFormat"/> is not applied.</summary>
public interface ICsvExporter
{
    /// <summary>Exports <paramref name="rows"/> as CSV.</summary>
    /// <param name="rows">The data rows.</param>
    /// <param name="columns">The column descriptors (at least one).</param>
    /// <param name="headers">Optional title lines above the table.</param>
    /// <param name="fileName">Optional download name; defaults to <c>report-{timestamp}.csv</c>.
    /// Pass an explicit name when guaranteed uniqueness is required (the default includes millisecond
    /// precision but is not collision-proof under concurrent calls).</param>
    /// <returns>The produced CSV file as an <see cref="ExportResult"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rows"/> or <paramref name="columns"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="columns"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">A non-numeric value appears in an aggregated column.</exception>
    ExportResult Export<T>(
        IEnumerable<T> rows,
        IReadOnlyList<ExportColumn<T>> columns,
        IEnumerable<ReportHeader>? headers = null,
        string? fileName = null);
}
