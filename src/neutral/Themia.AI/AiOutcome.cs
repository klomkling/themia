namespace Themia.AI;

/// <summary>What a completion attempt produced.</summary>
/// <remarks>
/// <see cref="Unspecified"/> is reserved and must never be returned by a real provider — as in every
/// other Themia enum, <c>0</c> being a valid value would make <c>default</c> pass
/// <see cref="Enum.IsDefined(Type, object)"/> for a field nobody actually set.
/// <para>
/// Four failure outcomes rather than one exception, because the correct caller response differs for
/// each: <see cref="Truncated"/> is real but partial output that should not ship as-is;
/// <see cref="Filtered"/> means retrying and failing over are both pointless because every provider
/// will refuse similar content; <see cref="ProviderLimit"/> means fail over without retrying this
/// provider, so a burst does not burn the rest of a quota on calls that cannot succeed;
/// <see cref="ProviderError"/> means retry with backoff, then fail over.
/// </para>
/// </remarks>
public enum AiOutcome
{
    /// <summary>Reserved. A real provider never returns this — treat it as a bug if seen.</summary>
    Unspecified = 0,

    /// <summary>The provider completed the request normally.</summary>
    Completed,

    /// <summary>The output hit its length limit. <c>Text</c> is partial but present.</summary>
    Truncated,

    /// <summary>The provider's safety filter refused the request. Retrying and failing over are both pointless.</summary>
    Filtered,

    /// <summary>Quota or rate limit exceeded. Fail over; do not retry this provider.</summary>
    ProviderLimit,

    /// <summary>Transport failure, timeout, or a 5xx response. Retry with backoff, then fail over.</summary>
    ProviderError,
}
