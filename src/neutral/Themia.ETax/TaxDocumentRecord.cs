namespace Themia.ETax;

/// <summary>A stored tax document as the store returns it. The snapshot columns are immutable; the rest is lifecycle.</summary>
public sealed record TaxDocumentRecord
{
    /// <summary>Document id.</summary>
    public required Guid Id { get; init; }

    /// <summary>Kind (part of the idempotency key).</summary>
    public required TaxDocumentKind Kind { get; init; }

    /// <summary>Triggering event id (part of the idempotency key).</summary>
    public required string SourceEventId { get; init; }

    /// <summary>Revision (part of the idempotency key).</summary>
    public required int Revision { get; init; }

    /// <summary>The issuing seller; the numbering scope and the same-seller rule for replacements and notes.</summary>
    public required string SellerId { get; init; }

    /// <summary>The allocated gapless number.</summary>
    public required string DocumentNumber { get; init; }

    /// <summary>The tax point.</summary>
    public required DateOnly IssueDate { get; init; }

    /// <summary>The exact stored snapshot text; <see cref="SnapshotSha256"/> is verified before every render.</summary>
    public required string SnapshotJson { get; init; }

    /// <summary>Schema version of <see cref="SnapshotJson"/>.</summary>
    public required int SnapshotSchemaVersion { get; init; }

    /// <summary>SHA-256 of the exact stored text.</summary>
    public required string SnapshotSha256 { get; init; }

    /// <summary>The pinned template version this document always renders with.</summary>
    public required string TemplateVersion { get; init; }

    /// <summary>Lifecycle state.</summary>
    public required TaxDocumentState State { get; init; }

    /// <summary>Where route A delivers the document. Mutable until <see cref="TaxDocumentState.Sent"/>.</summary>
    public string? BuyerEmail { get; init; }

    /// <summary>Holder of the current lease, if any.</summary>
    public Guid? ClaimToken { get; init; }

    /// <summary>When the lease lapses. A lease is live while this is in the future.</summary>
    public DateTimeOffset? ClaimExpiresAt { get; init; }

    /// <summary>Failed automatic attempts since issue or the last requeue.</summary>
    public int Attempts { get; init; }

    /// <summary>The earliest time the next attempt may be claimed (backoff).</summary>
    public DateTimeOffset? NextAttemptAt { get; init; }

    /// <summary>Code of the last failure: a code only, never a message.</summary>
    public string? LastErrorCode { get; init; }

    /// <summary>When the FIRST ORIGINAL was rendered. Never overwritten.</summary>
    public DateTimeOffset? OriginalRenderedAt { get; init; }

    /// <summary>Hash of the FIRST ORIGINAL. Never overwritten; later renders are in the evidence history.</summary>
    public string? PdfSha256 { get; init; }

    /// <summary>When the email was accepted.</summary>
    public DateTimeOffset? SentAt { get; init; }

    /// <summary>The email Message-ID, equal to the document id.</summary>
    public string? MessageId { get; init; }

    /// <summary>The document that replaced this one, when <see cref="State"/> is <see cref="TaxDocumentState.Cancelled"/>.</summary>
    public Guid? ReplacedByDocumentId { get; init; }

    /// <summary>When the row was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>True while a lease is held at <paramref name="now"/>.</summary>
    /// <param name="now">The time to evaluate at.</param>
    public bool HasLiveLease(DateTimeOffset now) => ClaimToken is not null && ClaimExpiresAt is { } expires && expires > now;
}

/// <summary>What <see cref="ITaxDocumentStore.InsertAsync"/> stores for a new document.</summary>
public sealed record NewTaxDocument
{
    /// <summary>Document id.</summary>
    public required Guid Id { get; init; }

    /// <summary>The idempotency key.</summary>
    public required TaxDocumentKey Key { get; init; }

    /// <summary>The issuing seller.</summary>
    public required string SellerId { get; init; }

    /// <summary>The allocated number.</summary>
    public required string DocumentNumber { get; init; }

    /// <summary>The tax point.</summary>
    public required DateOnly IssueDate { get; init; }

    /// <summary>The exact snapshot text to store.</summary>
    public required string SnapshotJson { get; init; }

    /// <summary>Schema version of the snapshot.</summary>
    public required int SnapshotSchemaVersion { get; init; }

    /// <summary>SHA-256 of <see cref="SnapshotJson"/>.</summary>
    public required string SnapshotSha256 { get; init; }

    /// <summary>The template version the document is pinned to.</summary>
    public required string TemplateVersion { get; init; }

    /// <summary>The recipient, if known at issue.</summary>
    public string? BuyerEmail { get; init; }

    /// <summary>When the row is created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}
