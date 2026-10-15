namespace Themia.ETax.Withholding;

/// <summary>A stored certificate, as the app's store returns it.</summary>
public sealed record WithholdingCertificateRecord
{
    /// <summary>The certificate id.</summary>
    public required Guid Id { get; init; }

    /// <summary>The triggering event's id (part of the idempotency key).</summary>
    public required string SourceEventId { get; init; }

    /// <summary>The revision (part of the idempotency key).</summary>
    public required int Revision { get; init; }

    /// <summary>The issuing payer.</summary>
    public required string PayerId { get; init; }

    /// <summary>The printed number.</summary>
    public required string DocumentNumber { get; init; }

    /// <summary>The issue date.</summary>
    public required DateOnly IssueDate { get; init; }

    /// <summary>The snapshot text, byte for byte as <see cref="WithholdingSnapshotSerializer"/> wrote it.</summary>
    public required string SnapshotJson { get; init; }

    /// <summary>The snapshot's schema version.</summary>
    public required int SnapshotSchemaVersion { get; init; }

    /// <summary>The SHA-256 of <see cref="SnapshotJson"/>.</summary>
    public required string SnapshotSha256 { get; init; }

    /// <summary>The template version the certificate was issued under.</summary>
    public required string TemplateVersion { get; init; }

    /// <summary>Issued or Cancelled.</summary>
    public required WithholdingCertificateState State { get; init; }

    /// <summary>The replacement's id once cancelled for replacement; write-once.</summary>
    public Guid? ReplacedByCertificateId { get; init; }

    /// <summary>When the row was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>A certificate to insert, in state Issued.</summary>
public sealed record NewWithholdingCertificate
{
    /// <summary>The certificate id.</summary>
    public required Guid Id { get; init; }

    /// <summary>The idempotency key.</summary>
    public required WithholdingCertificateKey Key { get; init; }

    /// <summary>The issuing payer.</summary>
    public required string PayerId { get; init; }

    /// <summary>The printed number.</summary>
    public required string DocumentNumber { get; init; }

    /// <summary>The issue date.</summary>
    public required DateOnly IssueDate { get; init; }

    /// <summary>The snapshot text to store byte for byte.</summary>
    public required string SnapshotJson { get; init; }

    /// <summary>The snapshot's schema version.</summary>
    public required int SnapshotSchemaVersion { get; init; }

    /// <summary>The SHA-256 of <see cref="SnapshotJson"/>.</summary>
    public required string SnapshotSha256 { get; init; }

    /// <summary>The template version the certificate is issued under.</summary>
    public required string TemplateVersion { get; init; }

    /// <summary>When the row is created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}
