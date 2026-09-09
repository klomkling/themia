using Microsoft.Extensions.Logging;

namespace Themia.AI.Internal;

/// <summary>
/// The <see cref="ITextTranslationService"/> that <c>AddThemiaAi</c> registers. Short-circuits blank
/// input and same-language requests without calling a provider, otherwise builds an <see cref="AiPrompt"/>
/// and dispatches through <see cref="IAiCompletionClient"/> for <see cref="AiOperation.Translation"/>,
/// mapping the result per design §4's table. Language codes are passed to the provider exactly as given
/// — this class holds no list of supported languages and normalises nothing.
/// </summary>
internal sealed class TranslationService(IAiCompletionClient client, ILogger<TranslationService> logger)
    : ITextTranslationService
{
    public async Task<TranslationResult> TranslateAsync(
        string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(sourceLanguage);
        ArgumentNullException.ThrowIfNull(targetLanguage);

        // A call that can only return nothing still costs a call (design §4).
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TranslationResult(TranslationOutcome.SameLanguage, text);
        }

        if (string.Equals(sourceLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return new TranslationResult(TranslationOutcome.SameLanguage, text);
        }

        var prompt = new AiPrompt
        {
            System = $"Translate the user's text from language '{sourceLanguage}' to '{targetLanguage}'. Return only the translated text, with no explanation or commentary.",
            User = text,
        };

        var completion = await client.CompleteAsync(AiOperation.Translation, prompt, cancellationToken)
            .ConfigureAwait(false);

        return Map(completion, sourceLanguage, targetLanguage);
    }

    /// <summary>The <see cref="AiOutcome"/> → <see cref="TranslationOutcome"/> mapping from design §4.</summary>
    private TranslationResult Map(AiCompletion completion, string sourceLanguage, string targetLanguage)
    {
        switch (completion.Outcome)
        {
            case AiOutcome.Completed:
                return new TranslationResult(TranslationOutcome.Translated, completion.Text);

            // Truncated carries non-empty Text, so the reflexive mapping is Translated — that stores a
            // sentence that stops mid-word under a label that says it is finished. It gets a member of
            // its own instead.
            case AiOutcome.Truncated:
                return new TranslationResult(TranslationOutcome.Incomplete, completion.Text);

            case AiOutcome.Filtered:
            case AiOutcome.ProviderLimit:
            case AiOutcome.ProviderError:
                logger.LogWarning(
                    "Translation from {SourceLanguage} to {TargetLanguage} was unavailable: {Outcome}.",
                    sourceLanguage, targetLanguage, completion.Outcome);
                // Unavailable carries Text = null, never the source text: returning the input would be
                // the helpful thing to do, and it is exactly what the placeholder this replaces did.
                return new TranslationResult(TranslationOutcome.Unavailable, null);

            case AiOutcome.Unspecified:
            default:
                throw new InvalidOperationException(
                    $"{nameof(IAiCompletionClient)} returned {nameof(AiOutcome)}.{completion.Outcome}, which a real call must never do.");
        }
    }
}
