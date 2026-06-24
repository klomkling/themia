using System.Globalization;

namespace Themia.Export.Internal;

/// <summary>Computes a column's summary-row cell. Shared by the CSV and Excel writers so both formats
/// produce identical numbers (no Excel SUM() formulas).</summary>
internal static class AggregateComputer
{
    /// <summary>Computes the summary cell for one column.</summary>
    /// <returns>A boxed <see cref="decimal"/> for numeric kinds, the <paramref name="title"/> for
    /// <see cref="AggregateKind.Label"/>, or <see langword="null"/> for <see cref="AggregateKind.None"/>
    /// or when no values participate.</returns>
    /// <exception cref="InvalidOperationException">A non-null value in a numeric aggregate is not convertible to a number.</exception>
    public static object? Compute(AggregateKind kind, string title, IEnumerable<object?> values)
    {
        if (kind == AggregateKind.None)
        {
            return null;
        }

        if (kind == AggregateKind.Label)
        {
            return title;
        }

        if (kind == AggregateKind.Count)
        {
            return (decimal)values.Count(v => v is not null);
        }

        var numbers = new List<decimal>();
        foreach (var value in values)
        {
            if (value is null)
            {
                continue;
            }

            if (value is bool or Enum)
            {
                throw new InvalidOperationException(
                    $"Column '{title}' has an {kind} aggregate but value '{value}' is not numeric.");
            }

            try
            {
                numbers.Add(Convert.ToDecimal(value, CultureInfo.InvariantCulture));
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                throw new InvalidOperationException(
                    $"Column '{title}' has an {kind} aggregate but value '{value}' is not numeric.", ex);
            }
        }

        if (numbers.Count == 0)
        {
            return null;
        }

        return kind switch
        {
            AggregateKind.Sum => numbers.Sum(),
            AggregateKind.Average => numbers.Sum() / numbers.Count,
            AggregateKind.Min => numbers.Min(),
            AggregateKind.Max => numbers.Max(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled aggregate kind."),
        };
    }
}
