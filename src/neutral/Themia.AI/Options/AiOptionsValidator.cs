using Microsoft.Extensions.Options;

namespace Themia.AI;

/// <summary>
/// Fails the host at startup when <see cref="AiOptions"/> and the registered
/// <see cref="IAiCompletionProvider"/>s cannot serve every operation. Design §6: validates the whole
/// provider &#215; operation matrix, not each entry alone — a failover list, a per-provider model, and a
/// per-provider timeout are each individually harmless and only fail together, in production, during
/// the exact rate-limit burst failover exists to survive.
/// </summary>
internal sealed class AiOptionsValidator(
    IEnumerable<IAiCompletionProvider> providers,
    IOptionsMonitor<AiProviderOptions> providerOptions) : IValidateOptions<AiOptions>
{
    public ValidateOptionsResult Validate(string? name, AiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Failover.Length == 0)
        {
            return options.AllowNoProvider
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(
                    $"{nameof(AiOptions.Failover)} is empty. Set {nameof(AiOptions.AllowNoProvider)} " +
                    "to true to run with no configured provider, or register at least one.");
        }

        var registered = providers.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        for (var i = 0; i < options.Failover.Length; i++)
        {
            var key = options.Failover[i];

            if (!registered.ContainsKey(key))
            {
                failures.Add(
                    $"{nameof(AiOptions.Failover)} names '{key}', but no {nameof(IAiCompletionProvider)} " +
                    "with that Key is registered.");
                continue;
            }

            var entry = providerOptions.Get(key);

            if (string.IsNullOrEmpty(entry.CompletionModel))
            {
                failures.Add(
                    $"Provider '{key}' has no {nameof(AiProviderOptions.CompletionModel)} configured " +
                    $"for {nameof(AiOperation.Completion)}.");
            }

            if (string.IsNullOrEmpty(entry.TranslationModel))
            {
                failures.Add(
                    $"Provider '{key}' has no {nameof(AiProviderOptions.TranslationModel)} configured " +
                    $"for {nameof(AiOperation.Translation)}.");
            }

            if (entry.Timeout <= TimeSpan.Zero)
            {
                failures.Add(
                    $"Provider '{key}' has a non-positive {nameof(AiProviderOptions.Timeout)} " +
                    $"({entry.Timeout}).");
            }

            // Only the first provider's worst case matters here: if it alone can exhaust the budget,
            // every provider after it in Failover is unreachable by arithmetic, whatever its own
            // timeout is.
            if (i == 0)
            {
                var worstCase = entry.Timeout * options.MaxRetriesPerProvider;
                if (worstCase > options.TotalBudget)
                {
                    failures.Add(
                        $"{nameof(AiOptions.MaxRetriesPerProvider)} ({options.MaxRetriesPerProvider}) x " +
                        $"provider '{key}' {nameof(AiProviderOptions.Timeout)} ({entry.Timeout}) = " +
                        $"{worstCase}, which exceeds {nameof(AiOptions.TotalBudget)} " +
                        $"({options.TotalBudget}). The rest of {nameof(AiOptions.Failover)} would be " +
                        "unreachable.");
                }
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
