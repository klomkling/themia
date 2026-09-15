using Themia.Content.Internal;

namespace Themia.Content;

/// <summary>
/// The languages content pages may be written in, and the language a reader falls back to when the requested
/// one has no published page.
/// </summary>
public sealed class ContentOptions
{
    /// <summary>Every language a page may be saved in, for example <c>th</c> and <c>en</c>. Values are compared
    /// and stored trimmed and lower-cased.</summary>
    public IList<string> Languages { get; } = new List<string>();

    /// <summary>The language served when the requested one has no published page. Must be one of
    /// <see cref="Languages"/>.</summary>
    public string FallbackLanguage { get; set; } = string.Empty;

    /// <summary>The fallback language, normalised.</summary>
    internal string NormalisedFallback => ContentLanguage.Normalise(FallbackLanguage);

    /// <summary>Whether <paramref name="normalisedLanguage"/> is one of the configured languages.</summary>
    internal bool IsConfigured(string normalisedLanguage) =>
        Languages.Any(language => ContentLanguage.Normalise(language) == normalisedLanguage);

    /// <summary>Throws when the configuration cannot serve a page.</summary>
    /// <exception cref="InvalidOperationException">No language; a blank, over-long or repeated language; or a
    /// fallback that is not one of the languages.</exception>
    internal void Validate()
    {
        var normalised = Languages.Select(ContentLanguage.Normalise).ToList();

        if (normalised.Count == 0)
        {
            throw new InvalidOperationException("ContentOptions.Languages must contain at least one language.");
        }

        if (normalised.Any(language => language.Length == 0))
        {
            throw new InvalidOperationException("ContentOptions.Languages must not contain a blank language.");
        }

        if (normalised.Any(language => language.Length > ContentLanguage.MaxLength))
        {
            throw new InvalidOperationException(
                $"ContentOptions.Languages entries must be at most {ContentLanguage.MaxLength} characters.");
        }

        if (normalised.Distinct(StringComparer.Ordinal).Count() != normalised.Count)
        {
            throw new InvalidOperationException("ContentOptions.Languages must not contain the same language twice.");
        }

        if (!normalised.Contains(NormalisedFallback, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"ContentOptions.FallbackLanguage '{FallbackLanguage}' must be one of ContentOptions.Languages.");
        }
    }
}
