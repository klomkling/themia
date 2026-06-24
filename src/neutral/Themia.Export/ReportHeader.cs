namespace Themia.Export;

/// <summary>A title line rendered above the table (merged across the column span in Excel; padded to the
/// column count in CSV so the file stays rectangular).</summary>
/// <param name="Line">The header text.</param>
public readonly record struct ReportHeader(string Line);
