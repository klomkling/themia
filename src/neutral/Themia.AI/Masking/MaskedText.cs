namespace Themia.AI;

/// <summary>The result of <see cref="IAiTextMasker.Mask"/>: masked text plus the map needed to restore it.</summary>
/// <param name="Text">The text with every masked value replaced by its token.</param>
/// <param name="Tokens">Maps each generated token (for example <c>&lt;x id="1"/&gt;</c>) to the original value it replaced.</param>
public sealed record MaskedText(string Text, IReadOnlyDictionary<string, string> Tokens)
{
    /// <summary>Substitutes the original values back into <paramref name="modelOutput"/>.</summary>
    /// <param name="modelOutput">Text derived from <see cref="Text"/> by a model call (for example a translation).</param>
    /// <returns><paramref name="modelOutput"/> with every token replaced by the value it stands for.</returns>
    /// <exception cref="InvalidOperationException">
    /// A token is missing (the model dropped it) or appears more than once (the model duplicated it).
    /// Restoring partial text is worse than the alternative: a leftover <c>&lt;x id="3"/&gt;</c> shown to
    /// a user is a visibly broken result, where the caller falling back to the untranslated original is
    /// visibly-not-translated but otherwise correct. There is nothing else for a caller to branch on, so
    /// this throws rather than returning a partial or an outcome enum.
    /// </exception>
    public string Restore(string modelOutput)
    {
        ArgumentNullException.ThrowIfNull(modelOutput);

        foreach (var token in Tokens.Keys)
        {
            var occurrences = CountOccurrences(modelOutput, token);
            if (occurrences == 0)
                throw new InvalidOperationException($"The model output is missing token '{token}'.");
            if (occurrences > 1)
                throw new InvalidOperationException($"The model output contains token '{token}' more than once.");
        }

        return MaskToken.Pattern.Replace(modelOutput, match =>
            Tokens.TryGetValue(match.Value, out var original) ? original : match.Value);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
