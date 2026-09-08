using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Themia.AI.Internal;

/// <summary>
/// The <see cref="IAiCompletionClient"/> that <c>AddThemiaAi</c> registers. Resolves the model per
/// provider per <see cref="AiOperation"/>, retries <see cref="AiOutcome.ProviderError"/> with backoff up
/// to <see cref="AiOptions.MaxRetriesPerProvider"/>, fails over to the next <see cref="AiOptions.Failover"/>
/// entry on <see cref="AiOutcome.ProviderLimit"/> and on exhausted <see cref="AiOutcome.ProviderError"/>,
/// and never retries or fails over on <see cref="AiOutcome.Filtered"/>. Design §3 and §6.
/// </summary>
/// <remarks>
/// <see cref="AiOptions.TotalBudget"/> bounds every retry and every failover for one
/// <see cref="CompleteAsync"/> call, via a <see cref="CancellationTokenSource"/> linked to the caller's
/// own token — the caller's cancellation and the budget's own expiry both cancel the same linked token,
/// so <see cref="CompleteAsync"/> tells them apart by re-checking the caller's original token: still
/// clear means the budget fired and the call returns <see cref="AiOutcome.ProviderError"/>; set means the
/// caller stopped caring and the call must propagate <see cref="OperationCanceledException"/> without
/// trying another provider.
/// </remarks>
internal sealed class FailoverCompletionClient(
    IEnumerable<IAiCompletionProvider> providers,
    IOptions<AiOptions> options,
    IOptionsMonitor<AiProviderOptions> providerOptions,
    ILogger<FailoverCompletionClient> logger) : IAiCompletionClient
{
    // Small and linear on purpose: AiOptionsValidator's worst-case arithmetic (MaxRetriesPerProvider x
    // Timeout) has no separate backoff term, and TotalBudget's own linked cancellation caps every delay
    // regardless — so backoff only has to be modest, not budgeted for explicitly.
    private static readonly TimeSpan BackoffUnit = TimeSpan.FromMilliseconds(200);

    public async Task<AiCompletion> CompleteAsync(
        AiOperation operation, AiPrompt prompt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var aiOptions = options.Value;
        var byKey = providers.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);

        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetCts.CancelAfter(aiOptions.TotalBudget);

        AiCompletion? last = null;

        foreach (var key in aiOptions.Failover)
        {
            if (!byKey.TryGetValue(key, out var provider))
            {
                // AiOptionsValidator rejects this at startup; guard anyway rather than throw mid-dispatch.
                logger.LogWarning("Failover names '{ProviderKey}' but no provider with that Key is registered; skipping.", key);
                continue;
            }

            var entry = providerOptions.Get(key);
            var model = ResolveModel(entry, operation);

            for (var attempt = 1; attempt <= aiOptions.MaxRetriesPerProvider; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (budgetCts.IsCancellationRequested)
                {
                    return BudgetExhausted(key, model);
                }

                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(budgetCts.Token);
                attemptCts.CancelAfter(entry.Timeout);

                AiCompletion result;
                try
                {
                    result = await provider.CompleteAsync(model, prompt, entry.Timeout, attemptCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The caller's own token stopped caring — propagate, never fail over (design §6).
                    cancellationToken.ThrowIfCancellationRequested();

                    if (budgetCts.IsCancellationRequested)
                    {
                        return BudgetExhausted(key, model);
                    }

                    // Neither the caller nor the budget fired, so this attempt's own Timeout did: a
                    // hang, not an exception. Map it to ProviderError so it retries/fails over like any
                    // other transport failure (design §6).
                    logger.LogWarning(
                        "Provider {ProviderKey} did not respond within {Timeout} on attempt {Attempt}/{MaxAttempts}.",
                        key, entry.Timeout, attempt, aiOptions.MaxRetriesPerProvider);
                    last = new AiCompletion(AiOutcome.ProviderError, null, null, model, "provider timeout");

                    if (attempt < aiOptions.MaxRetriesPerProvider)
                    {
                        var exhausted = await DelayBeforeRetryAsync(attempt, key, model, budgetCts.Token, cancellationToken)
                            .ConfigureAwait(false);
                        if (exhausted is not null) return exhausted;
                    }

                    continue;
                }

                last = result;

                switch (result.Outcome)
                {
                    case AiOutcome.Completed:
                    case AiOutcome.Truncated:
                    case AiOutcome.Filtered:
                        // Filtered stops here on purpose: a safety refusal is about the content, and the
                        // next provider will refuse it too, so retrying or failing over spends a call to
                        // receive the same answer.
                        return result;

                    case AiOutcome.ProviderLimit:
                        // Never retry a quota rejection: a burst would spend the rest of the allowance on
                        // calls that cannot succeed. Fail over immediately.
                        logger.LogInformation("Provider {ProviderKey} reported ProviderLimit; failing over.", key);
                        goto nextProvider;

                    case AiOutcome.ProviderError:
                        logger.LogWarning(
                            "Provider {ProviderKey} reported ProviderError on attempt {Attempt}/{MaxAttempts}.",
                            key, attempt, aiOptions.MaxRetriesPerProvider);

                        if (attempt < aiOptions.MaxRetriesPerProvider)
                        {
                            var exhausted = await DelayBeforeRetryAsync(attempt, key, model, budgetCts.Token, cancellationToken)
                                .ConfigureAwait(false);
                            if (exhausted is not null) return exhausted;
                        }

                        break;

                    case AiOutcome.Unspecified:
                    default:
                        throw new InvalidOperationException(
                            $"Provider '{key}' returned {nameof(AiOutcome)}.{result.Outcome}, which a real provider must never do.");
                }
            }

            nextProvider: ;
        }

        return last ?? new AiCompletion(AiOutcome.ProviderError, null, null, null, "no provider was tried");
    }

    /// <summary>Resolves the model this call uses from <paramref name="entry"/>, by <paramref name="operation"/> — the reason <see cref="AiOperation"/> travels on every call (design §3).</summary>
    private static string ResolveModel(AiProviderOptions entry, AiOperation operation) => operation switch
    {
        AiOperation.Completion => entry.CompletionModel,
        AiOperation.Translation => entry.TranslationModel,
        AiOperation.Unspecified => throw new ArgumentOutOfRangeException(
            nameof(operation), operation, $"{nameof(AiOperation)}.{nameof(AiOperation.Unspecified)} is reserved and must not be sent by a real call."),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, $"Unhandled {nameof(AiOperation)} value."),
    };

    /// <summary>
    /// Waits the backoff for <paramref name="attempt"/>, bounded by the same budget token as every
    /// provider attempt. Returns <see langword="null"/> to continue retrying, or the budget-exhausted
    /// result to return immediately. Throws if <paramref name="callerToken"/> — the caller's own,
    /// unlinked token — was the reason the wait was cut short.
    /// </summary>
    private async Task<AiCompletion?> DelayBeforeRetryAsync(
        int attempt, string key, string model, CancellationToken budgetToken, CancellationToken callerToken)
    {
        try
        {
            await Task.Delay(BackoffUnit * attempt, budgetToken).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException)
        {
            callerToken.ThrowIfCancellationRequested();
            return BudgetExhausted(key, model);
        }
    }

    /// <summary>
    /// The result <see cref="CompleteAsync"/> returns the moment <see cref="AiOptions.TotalBudget"/>
    /// expires, whatever providers in <see cref="AiOptions.Failover"/> remain untried (design §6).
    /// </summary>
    private AiCompletion BudgetExhausted(string key, string model)
    {
        logger.LogWarning(
            "TotalBudget exhausted while attempting provider {ProviderKey}; returning ProviderError without trying the rest of Failover.",
            key);
        return new AiCompletion(AiOutcome.ProviderError, null, null, model, "total budget exhausted");
    }
}
