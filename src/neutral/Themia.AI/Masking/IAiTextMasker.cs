namespace Themia.AI;

/// <summary>Reversibly replaces caller-supplied values with numbered tokens before text is sent to a model.</summary>
/// <remarks>
/// Masking is defence in depth, not the primary control — the app still decides which fields are sent
/// at all. Themia does not detect what to mask: Thai has no capitalisation and Thai person-name
/// detection is unreliable, so a detector would miss names silently. The caller, which knows a field is
/// <c>contact.Name</c>, can mask it exactly.
/// </remarks>
public interface IAiTextMasker
{
    /// <summary>
    /// Replaces every occurrence of each value in <paramref name="valuesToMask"/> with a numbered token,
    /// unique within the returned <see cref="MaskedText"/>.
    /// </summary>
    /// <param name="text">The text to mask. Treated as untrusted: it may already contain literal token-shaped text.</param>
    /// <param name="valuesToMask">The exact substrings to replace. Supplied by the caller — never inferred.</param>
    /// <returns>The masked text together with the map needed to restore it.</returns>
    MaskedText Mask(string text, IEnumerable<string> valuesToMask);
}
