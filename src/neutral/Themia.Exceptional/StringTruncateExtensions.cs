namespace Themia.Exceptional;

/// <summary>String helpers for bounding stored field lengths.</summary>
public static class StringTruncateExtensions
{
    /// <summary>Truncates <paramref name="value"/> to at most <paramref name="maxLength"/> characters. Null-safe.</summary>
    public static string? Truncate(this string? value, int maxLength)
        => value is null || value.Length <= maxLength ? value : value[..maxLength];
}
