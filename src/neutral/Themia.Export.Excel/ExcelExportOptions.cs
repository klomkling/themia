using ClosedXML.Excel;

namespace Themia.Export.Excel;

/// <summary>Workbook-level options for an Excel export.</summary>
public sealed class ExcelExportOptions
{
    /// <summary>The worksheet name. Defaults to <c>"Sheet1"</c>.</summary>
    public string SheetName { get; init; } = "Sheet1";

    /// <summary>The ClosedXML table theme. <see langword="null"/> uses <c>TableStyleMedium2</c>.</summary>
    public XLTableTheme? TableTheme { get; init; }

    /// <summary>The workbook font. Defaults to <c>"Calibri"</c>.</summary>
    public string FontName { get; init; } = "Calibri";

    /// <summary>Freeze the rows through the table header. Defaults to <see langword="true"/>.</summary>
    public bool FreezeHeaderRow { get; init; } = true;

    /// <summary>Column-sizing strategy. Defaults to <see cref="ColumnWidthMode.Estimate"/>.</summary>
    public ColumnWidthMode WidthMode { get; init; } = ColumnWidthMode.Estimate;

    /// <summary>Rows sampled when sizing columns (Estimate/Measure). Defaults to 50.</summary>
    public int WidthSampleRows { get; init; } = 50;
}
