namespace Themia.Content.Internal;

/// <summary>The one place a language value is normalised, so storage, lookup and configuration agree.</summary>
internal static class ContentLanguage
{
    /// <summary>The width of the <c>language</c> column: the practical ceiling for a BCP 47 tag.</summary>
    public const int MaxLength = 35;

    /// <summary>Trims and lower-cases <paramref name="language"/>; <see langword="null"/> becomes empty.</summary>
    public static string Normalise(string? language) => (language ?? string.Empty).Trim().ToLowerInvariant();
}
