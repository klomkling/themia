// Deliberately namespaced Themia.Messaging, not Themia.Messaging.Hmac: this type is not an HMAC
// concept. It lives in this ASSEMBLY because Themia.Messaging.Hmac is the only package both halves of
// the system already reference (the sending dispatcher and the receiving filter), and it has no project
// dependencies of its own. Putting it in the Themia.Messaging core instead would drag the outbox
// drainer, dialects and inbox admission into receive-only hosts that never publish anything.
namespace Themia.Messaging;

/// <summary>
/// This service's identity on the messaging fabric: stamped on every message it originates, and
/// compared by the receiving loop guard against the inbound <c>{prefix}Origin</c> header.
/// </summary>
/// <remarks>
/// <para>
/// Registered once, and read by both halves of the system — the outbox store that stamps outbound
/// messages and the verification filter that detects loopback. Holding it in one place is what makes
/// the two agree by construction: when the stamp and the comparison came from separate configuration
/// values, drift between them silently disabled loop protection, with no exception and no log.
/// </para>
/// <para>
/// <b>Renaming an origin is a fabric-wide operation, not a per-instance config change.</b> The outbox
/// stamps each row's <c>Origin</c> at enqueue time, not at delivery time, so rows already enqueued under
/// the old origin are delivered and compared against the new one after a redeploy, and the inbox's
/// <c>(origin, message_id)</c> dedup key resets across the rename too. Drain the outbox, and let the
/// inbox's dedup window pass, before rolling out an origin change.
/// </para>
/// </remarks>
public sealed class MessagingIdentity
{
    /// <summary>
    /// The longest accepted origin. Matches the <c>origin</c> column width in the outbox and inbox
    /// schemas, so an over-long value is refused at startup rather than failing at the first publish
    /// (PostgreSQL and SQL Server) or being silently truncated into a permanently non-matching origin
    /// (MySQL in non-strict mode).
    /// </summary>
    public const int MaxOriginLength = 100;

    /// <summary>Creates the identity.</summary>
    /// <param name="origin">
    /// This service's origin identifier, e.g. <c>propertiezy</c>. Surrounding whitespace is trimmed.
    /// Must be unique across every service on the fabric: two services sharing an origin makes each
    /// one's messages look like the other's loopback.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="origin"/> is null, empty or whitespace, or is longer than
    /// <see cref="MaxOriginLength"/> once trimmed.
    /// </exception>
    public MessagingIdentity(string origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);

        // Trimming is load-bearing, not cosmetic. HTTP strips optional whitespace around a header value
        // in transit (RFC 9110 §5.5), so an origin bound from config with a stray trailing space would be
        // stamped and sent padded but arrive trimmed — and LoopGuard's Ordinal comparison would then never
        // match, silently disabling loop protection in exactly the way this type exists to prevent.
        Origin = origin.Trim();

        if (Origin.Length > MaxOriginLength)
        {
            throw new ArgumentException(
                $"Must be at most {MaxOriginLength} characters (was {Origin.Length}) to fit the origin column "
                + "in the outbox and inbox schemas.",
                nameof(origin));
        }
    }

    /// <summary>This service's origin identifier, trimmed.</summary>
    public string Origin { get; }
}
