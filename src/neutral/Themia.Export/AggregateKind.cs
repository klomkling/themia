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
    /// <summary>Count of non-null values (null entries are excluded).</summary>
    Count,
    /// <summary>Arithmetic mean of non-null numeric values (null entries are excluded).</summary>
    Average,
    /// <summary>Smallest non-null numeric value (null entries are excluded).</summary>
    Min,
    /// <summary>Largest non-null numeric value (null entries are excluded).</summary>
    Max,
}
