namespace Themia.Export;

/// <summary>Describes one export column: a typed value selector plus presentation and aggregate intent.
/// No reflection — the consumer maps its own row type <typeparamref name="T"/> to columns.</summary>
/// <typeparam name="T">The row type.</typeparam>
public sealed class ExportColumn<T>
{
    /// <summary>The column header text.</summary>
    public required string Title { get; init; }

    /// <summary>Extracts the cell value from a row. May return <see langword="null"/>.</summary>
    public required Func<T, object?> Selector { get; init; }

    /// <summary>An Excel/.NET number-format string (e.g. <c>"#,##0.00"</c>); applied in Excel only.
    /// Ignored by the CSV backend.</summary>
    public string? NumberFormat { get; init; }

    /// <summary>Horizontal alignment; defaults to <see cref="ColumnAlignment.Auto"/>.</summary>
    public ColumnAlignment Alignment { get; init; } = ColumnAlignment.Auto;

    /// <summary>Explicit column width; <see langword="null"/> means the backend sizes it.
    /// Ignored by the CSV backend.</summary>
    public double? Width { get; init; }

    /// <summary>The summary-row operation; defaults to <see cref="AggregateKind.None"/>.</summary>
    public AggregateKind Aggregate { get; init; } = AggregateKind.None;
}
