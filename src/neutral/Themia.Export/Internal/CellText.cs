using System.Globalization;

namespace Themia.Export.Internal;

/// <summary>Shared text-rendering helpers used by the CSV and Excel writers.</summary>
internal static class CellText
{
    /// <summary>Renders <paramref name="value"/> to a locale-independent string.
    /// <c>IFormattable</c> values (numbers, dates, decimals) are formatted with
    /// <see cref="CultureInfo.InvariantCulture"/> and no format specifier so the raw value
    /// is preserved. <see langword="null"/> becomes <see cref="string.Empty"/>.</summary>
    public static string Invariant(object? value) => value switch
    {
        null => string.Empty,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
