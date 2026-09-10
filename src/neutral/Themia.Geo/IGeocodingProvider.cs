namespace Themia.Geo;

/// <summary>Resolves a free-text address or place description to a coordinate.</summary>
/// <remarks>
/// One provider ships (<c>Themia.Geo.Google</c>). Unlike <c>Themia.AI</c>, there is deliberately no
/// failover between providers: geocoding runs in a background backfill where a caller can simply stop
/// and resume on the next run, and the budget <see cref="GeocodeOutcome.ProviderLimit"/> protects is a
/// monthly quota the caller owns, not a per-minute rate limit that clears in seconds.
/// </remarks>
public interface IGeocodingProvider
{
    /// <summary>Resolves <paramref name="query"/> to a coordinate.</summary>
    /// <param name="query">A free-text address or place description.</param>
    /// <param name="options">Optional region/language bias. <see langword="null"/> uses the provider's defaults.</param>
    /// <param name="cancellationToken">Cancels the underlying request.</param>
    /// <returns>The outcome, the resolved point when found, and the provider's raw status.</returns>
    Task<GeocodeResult> GeocodeAsync(string query, GeocodeOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>The result of one <see cref="IGeocodingProvider.GeocodeAsync"/> call.</summary>
/// <param name="Outcome">What happened. Never <see cref="GeocodeOutcome.Unspecified"/> from a real provider.</param>
/// <param name="Point">The resolved coordinate when <paramref name="Outcome"/> is <see cref="GeocodeOutcome.Found"/>; otherwise <see langword="null"/>.</param>
/// <param name="ProviderStatus">The provider's own status text, for logging and diagnosis. Not part of the contract's meaning — only <paramref name="Outcome"/> is.</param>
public sealed record GeocodeResult(GeocodeOutcome Outcome, GeoPoint? Point, string? ProviderStatus);

/// <summary>Per-call hints for <see cref="IGeocodingProvider.GeocodeAsync"/>.</summary>
/// <remarks>
/// Nothing else belongs here: parameters that vary per provider stay in that provider's own options
/// type (e.g. <c>GoogleGeocodingOptions</c> for the API key).
/// </remarks>
public sealed record GeocodeOptions
{
    /// <summary>ISO 3166-1 alpha-2 region bias, e.g. <c>"TH"</c>. Biases results; does not restrict them.</summary>
    public string? Region { get; init; }

    /// <summary>Language for the provider's own response text, e.g. <c>"th"</c>.</summary>
    public string? Language { get; init; }
}

/// <summary>What a geocoding attempt produced.</summary>
/// <remarks>
/// <see cref="Unspecified"/> is reserved and must never be returned by a real provider — as in every
/// other Themia enum, <c>0</c> being a valid value would make <c>default</c> pass <c>Enum.IsDefined</c>
/// for a field nobody actually set.
/// <para>
/// <see cref="ProviderLimit"/> is kept separate from <see cref="ProviderError"/> because the correct
/// caller response differs: a batch backfill that treats a quota rejection as a transient error will
/// retry it for every remaining row and burn the rest of the day's allowance on calls that cannot
/// succeed. Collapsing the two removes the caller's ability to tell them apart.
/// </para>
/// </remarks>
public enum GeocodeOutcome
{
    /// <summary>Reserved. A real provider never returns this — treat it as a bug if seen.</summary>
    Unspecified = 0,

    /// <summary>The provider resolved the query to a coordinate.</summary>
    Found,

    /// <summary>The provider answered; nothing matched.</summary>
    NotFound,

    /// <summary>Quota or rate limit exceeded. The caller should stop, not retry the batch.</summary>
    ProviderLimit,

    /// <summary>Transport failure or an unexpected provider response. A retry may work.</summary>
    ProviderError,
}
