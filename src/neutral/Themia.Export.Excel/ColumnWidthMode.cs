namespace Themia.Export.Excel;

/// <summary>How the Excel writer sizes columns that have no explicit <see cref="Themia.Export.ExportColumn{T}.Width"/>.</summary>
public enum ColumnWidthMode
{
    /// <summary>Font-free width estimated from the column title and sampled cell character lengths. Deterministic; CI-safe. Default.</summary>
    Estimate,
    /// <summary>ClosedXML glyph measurement over the sampled rows (needs font metrics).</summary>
    Measure,
    /// <summary>Leave default widths.</summary>
    None,
}
