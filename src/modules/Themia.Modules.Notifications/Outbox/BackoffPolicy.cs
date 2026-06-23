namespace Themia.Modules.Notifications.Outbox;

/// <summary>Exponential backoff and the dead-letter decision for outbox retries.</summary>
internal static class BackoffPolicy
{
    /// <summary>Maximum delay between attempts (cap on the exponential growth).</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(15);

    /// <summary>Next attempt time: <c>now + min(2^attempts seconds, MaxDelay)</c>.</summary>
    public static DateTimeOffset NextAttemptAt(DateTimeOffset now, int attempts)
    {
        var seconds = Math.Pow(2, Math.Min(attempts, 20)); // guard overflow
        var delay = TimeSpan.FromSeconds(seconds);
        if (delay > MaxDelay) delay = MaxDelay;
        return now + delay;
    }

    /// <summary>True when attempts have reached the configured cap.</summary>
    public static bool IsDead(int attempts, int maxAttempts) => attempts >= maxAttempts;
}
