namespace Themia.ETax;

/// <summary>Exponential backoff with jitter for retried sends.</summary>
internal static class TaxDocumentBackoff
{
    private const double JitterSpread = 0.4;

    /// <summary>The wait after the <paramref name="attempt"/>-th failed attempt: initial * 2^(attempt-1), scaled by 0.8..1.2, capped.</summary>
    /// <param name="attempt">1 for the first failure.</param>
    /// <param name="initial">The first wait.</param>
    /// <param name="max">The cap.</param>
    /// <param name="jitter">A value in [0, 1).</param>
    public static TimeSpan Delay(int attempt, TimeSpan initial, TimeSpan max, double jitter)
    {
        var doublings = Math.Min(attempt - 1, 30);
        var ticks = Math.Min(initial.Ticks * Math.Pow(2, doublings), max.Ticks);
        var jittered = ticks * ((1 - (JitterSpread / 2)) + (JitterSpread * jitter));
        return TimeSpan.FromTicks((long)Math.Min(jittered, max.Ticks));
    }
}
