using System.Text.RegularExpressions;

namespace Themia.AI;

/// <summary>Default <see cref="IAiTextMasker"/>. Tokens are XLIFF-style (<c>&lt;x id="1"/&gt;</c>).</summary>
/// <remarks>
/// A model trained on translation memory data leaves an XLIFF-style tag intact through a translation.
/// A human-readable placeholder such as <c>{{NAME}}</c> does not survive: the model renders it into the
/// target language and the substitution back fails silently, so the token shape here is load-bearing,
/// not cosmetic.
/// </remarks>
public sealed class AiTextMasker : IAiTextMasker
{
    /// <inheritdoc/>
    /// <remarks>
    /// Scans <paramref name="text"/> for token-shaped substrings first and numbers new tokens from
    /// beyond the highest one found. Without this scan, a value in the source that happens to already
    /// look like <c>&lt;x id="1"/&gt;</c> — whether by accident or a deliberate attempt — would collide
    /// with a generated token, and <see cref="MaskedText.Restore"/> would see that token twice and
    /// throw. That is a denial an ordinary caller can trigger with one line of untrusted text.
    /// </remarks>
    public MaskedText Mask(string text, IEnumerable<string> valuesToMask)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(valuesToMask);

        var values = valuesToMask.Where(value => !string.IsNullOrEmpty(value)).ToList();
        if (values.Count == 0)
            return new MaskedText(text, new Dictionary<string, string>(StringComparer.Ordinal));

        var nextId = NextAvailableTokenId(text);
        var valuePattern = new Regex(string.Join('|', values.Select(Regex.Escape)));

        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        var maskedText = valuePattern.Replace(text, match =>
        {
            var token = MaskToken.Create(nextId++);
            tokens[token] = match.Value;
            return token;
        });

        return new MaskedText(maskedText, tokens);
    }

    private static int NextAvailableTokenId(string text)
    {
        var maxId = 0;
        foreach (Match match in MaskToken.Pattern.Matches(text))
        {
            var id = int.Parse(match.Groups[1].Value);
            if (id > maxId)
                maxId = id;
        }

        return maxId + 1;
    }
}

/// <summary>The token shape shared by <see cref="AiTextMasker.Mask"/> and <see cref="MaskedText.Restore"/>.</summary>
internal static class MaskToken
{
    /// <summary>Matches any token, capturing its numeric id.</summary>
    internal static readonly Regex Pattern = new("""<x id="(\d+)"/>""", RegexOptions.Compiled);

    /// <summary>Formats the token for <paramref name="id"/>.</summary>
    internal static string Create(int id) => $"""<x id="{id}"/>""";
}
