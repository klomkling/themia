namespace Themia.Export;

/// <summary>The summary-row operation for a column. See the aggregate semantics in the design spec.</summary>
public enum AggregateKind
{
    /// <summary>No summary cell for this column.</summary>
    None,
    /// <summary>Write the column title as literal text (e.g. "Total").</summary>
    Label,
    /// <summary>Sum of the numeric values.</summary>
    Sum,
    /// <summary>Count of non-null values.</summary>
    Count,
    /// <summary>Arithmetic mean of the numeric values.</summary>
    Average,
    /// <summary>Smallest numeric value.</summary>
    Min,
    /// <summary>Largest numeric value.</summary>
    Max,
}
