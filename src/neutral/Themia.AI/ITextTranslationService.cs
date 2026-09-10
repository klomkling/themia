namespace Themia.AI;

/// <summary>Translates text between two languages, with an outcome a caller cannot mistake for success.</summary>
/// <remarks>
/// The one typed operation in this package (design §4): text in, text out, no domain vocabulary. A
/// caption service is deliberately not here — captioning needs listing type, price and amenities, which
/// is application domain, not framework concern.
/// </remarks>
public interface ITextTranslationService
{
    /// <summary>Translates <paramref name="text"/> from <paramref name="sourceLanguage"/> to <paramref name="targetLanguage"/>.</summary>
    /// <param name="text">
    /// The text to translate. Empty or whitespace-only text short-circuits to
    /// <see cref="TranslationOutcome.SameLanguage"/> with the input returned, without calling a
    /// provider — a call that can only return nothing still costs a call.
    /// </param>
    /// <param name="sourceLanguage">
    /// The source language code, passed to the provider exactly as given. Themia keeps no list of
    /// supported languages — such a list goes stale and differs per provider and model.
    /// </param>
    /// <param name="targetLanguage">The target language code, passed to the provider exactly as given.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// A <see cref="TranslationResult"/> whose <see cref="TranslationResult.Outcome"/> a caller must
    /// read before storing <see cref="TranslationResult.Text"/> — see <see cref="TranslationOutcome"/>.
    /// </returns>
    Task<TranslationResult> TranslateAsync(
        string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default);
}

/// <summary>The result of one <see cref="ITextTranslationService.TranslateAsync"/> call.</summary>
/// <param name="Outcome">What happened. Determines whether <paramref name="Text"/> is safe to store.</param>
/// <param name="Text">
/// The text, when <paramref name="Outcome"/> is <see cref="TranslationOutcome.Translated"/>,
/// <see cref="TranslationOutcome.SameLanguage"/> or <see cref="TranslationOutcome.Incomplete"/>;
/// <see langword="null"/> when <paramref name="Outcome"/> is <see cref="TranslationOutcome.Unavailable"/>.
/// </param>
public sealed record TranslationResult(TranslationOutcome Outcome, string? Text);

/// <summary>What a translation attempt produced.</summary>
/// <remarks>
/// <see cref="Unspecified"/> is reserved and never returned by a real call, as with every other Themia
/// enum. <see cref="SameLanguage"/> and <see cref="Unavailable"/> are kept separate on purpose — both
/// concern input text being handed back, but only one of them means the caller may store anything. That
/// is the distinction the placeholder this package replaces could not make (design §1, §4): its
/// <c>Task&lt;string&gt;</c> signature returned the source text on failure, so a caller could not tell
/// "translated" from "handed you back what you gave me", and stored Thai into a row labelled English.
/// </remarks>
public enum TranslationOutcome
{
    /// <summary>Reserved. A real call never returns this — treat it as a bug if seen.</summary>
    Unspecified = 0,

    /// <summary>Text is complete and storable.</summary>
    Translated,

    /// <summary>Source and target language match. <see cref="TranslationResult.Text"/> is the input, unchanged — and that is correct.</summary>
    SameLanguage,

    /// <summary>Text exists and is cut mid-sentence at the output length limit. DO NOT store.</summary>
    Incomplete,

    /// <summary>
    /// No provider, the provider failed, or it reported success with no text at all.
    /// <see cref="TranslationResult.Text"/> is null.
    /// </summary>
    Unavailable,
}
