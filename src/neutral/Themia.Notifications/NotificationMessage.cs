namespace Themia.Notifications;

/// <summary>A single notification to send. Either <see cref="Body"/> is pre-rendered, or
/// <see cref="Template"/> + <see cref="Model"/> are merged by an <see cref="INotificationTemplateRenderer"/>.</summary>
public sealed class NotificationMessage
{
    /// <summary>
    /// Headers a sender writes itself. Supplying one is silently wrong in one of three ways — verified
    /// against <c>System.Net.Mail</c> pickup-directory output rather than assumed:
    /// <list type="bullet">
    /// <item>the sender's value wins and the caller's is DISCARDED with no error —
    /// <c>To</c>, <c>From</c>, <c>Subject</c>, <c>Date</c>, <c>MIME-Version</c>, <c>Content-Type</c>;</item>
    /// <item>the caller's value wins and REPLACES the generated one — <c>Message-ID</c>;</item>
    /// <item>both are written and the message carries two conflicting headers —
    /// <c>Content-Transfer-Encoding</c>.</item>
    /// </list>
    /// None of the three reports anything, so all are refused at the call site instead.
    /// </summary>
    private static readonly HashSet<string> ReservedHeaderNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "To", "From", "Subject", "Date", "Message-ID",
            "MIME-Version", "Content-Type", "Content-Transfer-Encoding",
        };

    private readonly IReadOnlyDictionary<string, string>? _headers;

    /// <summary>The delivery channel.</summary>
    public NotificationChannel Channel { get; init; }

    /// <summary>The recipient address (email address, phone number, or user id for in-app).</summary>
    public string Recipient { get; init; } = string.Empty;

    /// <summary>Subject line (email); ignored by channels without a subject.</summary>
    public string? Subject { get; init; }

    /// <summary>Pre-rendered body. When set, it is used verbatim and <see cref="Template"/> is ignored.</summary>
    public string? Body { get; init; }

    /// <summary>Handlebars template source, merged with <see cref="Model"/> when <see cref="Body"/> is null.</summary>
    public string? Template { get; init; }

    /// <summary>The model merged into <see cref="Template"/>.</summary>
    public object? Model { get; init; }

    /// <summary>Optional channel/provider metadata (e.g. cc, sender id).</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>
    /// Extra headers to attach to THIS message, written verbatim by senders that have a header concept
    /// (<c>SmtpEmailSender</c> puts them on the MIME message). Null by default.
    /// </summary>
    /// <remarks>
    /// The motivating case is a per-message provider directive that config cannot express, because it
    /// differs per message rather than per deployment — Amazon SES reads
    /// <c>X-SES-CONFIGURATION-SET</c> to decide which configuration set (and therefore which reputation
    /// stream and event destination) a message belongs to. The alternative, a default configuration set
    /// on the SES identity, applies one to every message from the domain, so transactional and marketing
    /// mail share a complaint rate and a campaign can take passcode delivery down with it.
    /// <para>
    /// Distinct from <see cref="Metadata"/>: this reaches the wire as headers on the message, whereas
    /// <see cref="Metadata"/> is channel/provider hint data that no built-in sender transmits.
    /// </para>
    /// <para>
    /// Validated on assignment, so an invalid header cannot reach ANY sender rather than only the one
    /// that happens to be registered — an injection attempt must not depend on whether the host has SMTP
    /// configured. Names must be non-empty printable ASCII without <c>:</c> or whitespace, values must
    /// not contain CR or LF, and neither may name a header the sender writes itself (see
    /// <see cref="ReservedHeaderNames"/>). CR/LF is rejected rather than stripped: a value carrying
    /// <c>\r\n</c> ends the header and lets a caller append arbitrary headers — and after a blank line,
    /// an arbitrary body — so any consumer deriving a value from user input needs to learn at the call
    /// site, not have the payload quietly sanitised into something that still sends.
    /// </para>
    /// <para>
    /// Senders without a header concept (the logger stubs, SMS/push providers) accept this and ignore
    /// it; a consumer sets it unconditionally, so throwing there would turn running without a configured
    /// provider — a supported state — into a crash.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">A header name or value is invalid, or names a reserved header.</exception>
    public IReadOnlyDictionary<string, string>? Headers
    {
        get => _headers;
        init => _headers = value is null ? null : ValidateAndCopy(value);
    }

    // Copies rather than storing the caller's instance: an IReadOnlyDictionary is only read-only through
    // THIS reference, so a caller holding the underlying Dictionary could mutate it after assignment and
    // walk an injected value straight past the checks below. Validating something we then keep a
    // reference to would make the guarantee nominal.
    private static IReadOnlyDictionary<string, string> ValidateAndCopy(IReadOnlyDictionary<string, string> headers)
    {
        var copy = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (name, headerValue) in headers)
        {
            if (string.IsNullOrEmpty(name) || !name.All(IsHeaderNameChar))
            {
                throw new ArgumentException(
                    $"Header name '{name}' is not a valid header name: names must be non-empty printable "
                    + "ASCII without ':' or whitespace.",
                    nameof(Headers));
            }

            if (ReservedHeaderNames.Contains(name))
            {
                throw new ArgumentException(
                    $"Header '{name}' is written by the sender itself. Supplying it here is silently "
                    + "wrong: depending on the header, the sender's value wins and yours is dropped, "
                    + "yours replaces the sender's, or the message goes out carrying both.",
                    nameof(Headers));
            }

            if (headerValue is null || headerValue.AsSpan().IndexOfAny('\r', '\n') >= 0)
            {
                throw new ArgumentException(
                    $"Header '{name}' has a null value, or one containing CR or LF. A value carrying a "
                    + "line break ends the header and injects arbitrary headers into the message.",
                    nameof(Headers));
            }

            copy[name] = headerValue;
        }

        return copy;
    }

    // RFC 5322 field name: printable US-ASCII (33-126) excluding ':'. Excludes CR, LF and space by
    // construction, so the injection and blank-name cases fall out of the same check.
    private static bool IsHeaderNameChar(char c) => c is >= '!' and <= '~' && c != ':';
}
