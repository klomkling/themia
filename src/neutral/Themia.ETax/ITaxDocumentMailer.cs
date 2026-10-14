namespace Themia.ETax;

/// <summary>The e-Tax by Email message for route A.</summary>
public sealed record TaxDocumentEmail
{
    /// <summary>The document id; used as the email Message-ID so a resend is recognisable.</summary>
    public required Guid DocumentId { get; init; }

    /// <summary>The document number, for the subject.</summary>
    public required string DocumentNumber { get; init; }

    /// <summary>The kind, for the subject.</summary>
    public required TaxDocumentKind Kind { get; init; }

    /// <summary>The buyer's address.</summary>
    public required string To { get; init; }

    /// <summary>The Revenue Department's receiving address, from configuration.</summary>
    public required string RevenueDepartmentAddress { get; init; }

    /// <summary>The ORIGINAL PDF.</summary>
    public required byte[] Pdf { get; init; }

    /// <summary>When this replaces a cancelled document, its number, so the body can say so.</summary>
    public string? ReplacesDocumentNumber { get; init; }
}

/// <summary>How a send attempt ended.</summary>
public enum TaxDocumentSendOutcome
{
    /// <summary>The transport accepted the message.</summary>
    Sent = 0,

    /// <summary>A temporary problem (SMTP 4xx, timeout, connection refused): retry with backoff.</summary>
    Transient = 1,

    /// <summary>A permanent problem (SMTP 5xx, invalid recipient): do not retry.</summary>
    Permanent = 2,
}

/// <summary>The mailer's answer. Carries a code only, never a transport message (those can echo the recipient).</summary>
/// <param name="Outcome">How the attempt ended.</param>
/// <param name="ErrorCode">A short stable code for <see cref="TaxDocumentSendOutcome.Transient"/> and <see cref="TaxDocumentSendOutcome.Permanent"/>.</param>
public sealed record TaxDocumentSendResult(TaxDocumentSendOutcome Outcome, string? ErrorCode = null)
{
    /// <summary>The transport accepted the message.</summary>
    public static TaxDocumentSendResult Sent { get; } = new(TaxDocumentSendOutcome.Sent);

    /// <summary>A temporary failure.</summary>
    /// <param name="errorCode">A short stable code.</param>
    public static TaxDocumentSendResult Transient(string errorCode) => new(TaxDocumentSendOutcome.Transient, errorCode);

    /// <summary>A permanent failure.</summary>
    /// <param name="errorCode">A short stable code.</param>
    public static TaxDocumentSendResult Permanent(string errorCode) => new(TaxDocumentSendOutcome.Permanent, errorCode);
}

/// <summary>Sends the e-Tax email. Implemented by the app over its own transport.</summary>
public interface ITaxDocumentMailer
{
    /// <summary>Sends <paramref name="email"/> once. Do not retry inside: Themia owns retries and backoff.</summary>
    /// <param name="email">The message.</param>
    /// <param name="ct">
    /// MUST be honoured: it is cancelled when <see cref="TaxDocumentSendOptions.SendTimeout"/> elapses. The send runs under a lease
    /// that protects the document from being cancelled; an implementation that ignores the token and keeps going can deliver the
    /// email after that protection is gone.
    /// </param>
    Task<TaxDocumentSendResult> SendAsync(TaxDocumentEmail email, CancellationToken ct = default);
}
