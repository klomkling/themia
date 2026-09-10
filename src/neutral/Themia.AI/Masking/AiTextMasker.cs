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
    // The highest id a token already in the source is allowed to push the counter to. Half of int.MaxValue
    // leaves over a billion ids before the counter could wrap, which no single string can consume.
    private const int MaxScannedTokenId = int.MaxValue / 2;

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

        // Longest first. .NET alternation takes the FIRST alternative that matches at a position, not
        // the longest, so ["สมชาย", "สมชาย ใจดี"] in that order masks the given name and sends the
        // family name to the provider in clear — for an API whose whole job is keeping those values out
        // of the request, the worst way to fail. Ordering by length makes the longer value win wherever
        // two candidates start at the same place, whatever order the caller listed them in.
        var valuePattern = new Regex(string.Join('|', values.OrderByDescending(value => value.Length).Select(Regex.Escape)));

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
            // The ids come out of untrusted text and (\d+) bounds neither their length nor their value.
            // int.Parse threw OverflowException out of Mask on <x id="99999999999999999999"/>, and
            // <x id="2147483647"/> was worse than a throw: maxId + 1 wrapped to int.MinValue, the
            // generated tokens read <x id="-2147483648"/>, MaskToken.Pattern could not match them, and
            // Restore left the token in place — the user shown a placeholder where their own data should
            // be, with nothing raised anywhere. An id at or above the ceiling is skipped instead: it
            // cannot collide with a generated one, because reaching it would take more replacements than
            // a string has positions.
            if (!int.TryParse(match.Groups[1].Value, out var id) || id > MaxScannedTokenId)
                continue;

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
