namespace Themia.ETax.Withholding;

/// <summary>What to issue.</summary>
public sealed record IssueWithholdingCertificateRequest
{
    /// <summary>The idempotency key.</summary>
    public required WithholdingCertificateKey Key { get; init; }

    /// <summary>The issuing payer's id in the app (scopes the number run, the key and every read).</summary>
    public required string PayerId { get; init; }

    /// <summary>
    /// The series (the form's เล่มที่ used as a short code such as <c>CR</c> or <c>SF</c>), or <see langword="null"/> for none. Each series has its
    /// own gapless run per payer and period, and is printed as เล่มที่ (it is stored as the snapshot's <see cref="WithholdingCertificateSnapshot.BookNumber"/>).
    /// 1 to 20 letters, digits, hyphens or underscores. A <see cref="WithholdingCertificateSnapshot.BookNumber"/> in the snapshot must be empty or equal.
    /// </summary>
    public string? Series { get; init; }

    /// <summary>
    /// The signature image this certificate is issued with, or <see langword="null"/> for none. Only its SHA-256 is recorded (next to the snapshot,
    /// outside its hash, immutable); the app keeps the image. Every later render of the certificate must use exactly this image, so an old certificate
    /// is never re-rendered with a new signature if the signer changes.
    /// </summary>
    public WithholdingSignatureImage? Signature { get; init; }

    /// <summary>
    /// The certificate content. Its <see cref="WithholdingCertificateSnapshot.DocumentNumber"/> and <see cref="WithholdingCertificateSnapshot.IssueDate"/> are ignored: the number is allocated here and the issue date is today's date in Bangkok from the service's clock.
    /// The service copies the snapshot on entry, so later changes to the caller's lists do not reach what is validated or stored.
    /// </summary>
    public required WithholdingCertificateSnapshot Snapshot { get; init; }
}

/// <summary>The result of an issue attempt.</summary>
public enum WithholdingIssueOutcome
{
    /// <summary>Issued in the caller's transaction (not yet committed).</summary>
    Issued = 0,

    /// <summary>A certificate with this key already exists for this payer; nothing was consumed.</summary>
    AlreadyIssued = 1,

    /// <summary>The snapshot failed validation; see <see cref="WithholdingIssueResult.Errors"/>.</summary>
    ValidationFailed = 2,

    /// <summary>The replacement does not match a certificate of this payer that may be replaced (unknown, other payer, number or date differ, wrong source or revision, already cancelled).</summary>
    ReplacementInvalid = 3,

    /// <summary>The key exists but belongs to another payer. The other payer's id is never returned.</summary>
    SourceConflict = 4,
}

/// <summary>The outcome of <see cref="WithholdingCertificateService.IssueAsync"/>.</summary>
public sealed record WithholdingIssueResult
{
    /// <summary>What happened. Roll back unless it is <see cref="WithholdingIssueOutcome.Issued"/> or <see cref="WithholdingIssueOutcome.AlreadyIssued"/>.</summary>
    public required WithholdingIssueOutcome Outcome { get; init; }

    /// <summary>The certificate id for <see cref="WithholdingIssueOutcome.Issued"/> and <see cref="WithholdingIssueOutcome.AlreadyIssued"/>.</summary>
    public Guid? DocumentId { get; init; }

    /// <summary>The failed checks of <see cref="WithholdingIssueOutcome.ValidationFailed"/>; codes and field paths only.</summary>
    public IReadOnlyList<WithholdingValidationError> Errors { get; init; } = [];
}
