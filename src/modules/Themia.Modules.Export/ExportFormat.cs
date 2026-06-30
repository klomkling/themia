namespace Themia.Modules.Export;

/// <summary>The output format an export produces.</summary>
public enum ExportFormat
{
    /// <summary>CSV via <c>Themia.Export</c>.</summary>
    Csv,
    /// <summary>Excel <c>.xlsx</c> via <c>Themia.Export.Excel</c>.</summary>
    Xlsx,
}
