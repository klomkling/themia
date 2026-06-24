namespace Themia.Export.Internal;

/// <summary>Projects rows into a dense <c>object?[][]</c> matrix (one array per row) via the typed
/// selectors, in a single pass. No reflection.
/// <para>Full materialization is intentional: the Excel bulk-insert and the column aggregate
/// computations both need the complete data set, and streaming is out of scope by design.</para></summary>
internal static class RowProjector
{
    /// <summary>Materializes <paramref name="rows"/> into a matrix using each column's selector.</summary>
    public static object?[][] Project<T>(IEnumerable<T> rows, IReadOnlyList<ExportColumn<T>> columns)
    {
        var colCount = columns.Count;

        // When the count is known up front, allocate the outer array once and fill by index
        // (no List overhead, no ToArray copy). Fall back to a List for unknown-count sequences.
        if (rows.TryGetNonEnumeratedCount(out var n))
        {
            var result = new object?[n][];
            var i = 0;
            foreach (var row in rows)
            {
                var cells = new object?[colCount];
                for (var c = 0; c < colCount; c++)
                {
                    cells[c] = columns[c].Selector(row);
                }

                result[i++] = cells;
            }

            return result;
        }

        var matrix = new List<object?[]>();
        foreach (var row in rows)
        {
            var cells = new object?[colCount];
            for (var c = 0; c < colCount; c++)
            {
                cells[c] = columns[c].Selector(row);
            }

            matrix.Add(cells);
        }

        return matrix.ToArray();
    }

    /// <summary>Enumerates a single column slice of the materialized <paramref name="matrix"/>.</summary>
    public static IEnumerable<object?> Column(object?[][] matrix, int index)
    {
        foreach (var row in matrix)
        {
            yield return row[index];
        }
    }
}
