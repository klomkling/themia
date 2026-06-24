namespace Themia.Export.Internal;

/// <summary>Projects rows into a dense <c>object?[][]</c> matrix (one array per row) via the typed
/// selectors, in a single pass. No reflection.</summary>
internal static class RowProjector
{
    /// <summary>Materializes <paramref name="rows"/> into a matrix using each column's selector.</summary>
    public static object?[][] Project<T>(IEnumerable<T> rows, IReadOnlyList<ExportColumn<T>> columns)
    {
        var colCount = columns.Count;
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
}
