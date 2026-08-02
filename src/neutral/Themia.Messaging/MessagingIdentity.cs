namespace Themia.Messaging;

/// <summary>
/// This service's identity on the messaging fabric: stamped on every message it originates, and
/// compared by the receiving loop guard against the inbound <c>{prefix}Origin</c> header.
/// </summary>
/// <remarks>
/// Registered once, and read by both halves of the system — the outbox store that stamps outbound
/// messages and the verification filter that detects loopback. Holding it in one place is what makes
/// the two agree by construction: when the stamp and the comparison came from separate configuration
/// values, drift between them silently disabled loop protection, with no exception and no log.
/// </remarks>
/// <remarks>
/// The outbox stamps each row's <c>Origin</c> at enqueue time, not at delivery time, so renaming this
/// value is a fabric-wide operation, not a per-instance config change: rows already enqueued under the
/// old origin are delivered and compared against the new one after a redeploy, and the inbox's
/// <c>(origin, message_id)</c> dedup key resets across the rename too. Drain the outbox (and let the
/// inbox's dedup window pass) before rolling out an origin change.
/// </remarks>
public sealed class MessagingIdentity
{
    /// <summary>Creates the identity.</summary>
    /// <param name="origin">
    /// This service's origin identifier, e.g. <c>propertiezy</c>. Must be unique across every service
    /// on the fabric: two services sharing an origin makes each one's messages look like the other's
    /// loopback.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="origin"/> is null, empty or whitespace.</exception>
    public MessagingIdentity(string origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        Origin = origin;
    }

    /// <summary>This service's origin identifier.</summary>
    public string Origin { get; }
}
