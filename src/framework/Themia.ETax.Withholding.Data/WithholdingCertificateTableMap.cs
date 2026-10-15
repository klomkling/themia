using System.Text.RegularExpressions;
using Themia.ETax.Data;

namespace Themia.ETax.Withholding.Data;

/// <summary>
/// Names the columns of an app's own withholding certificate table, so the immutability trigger and the same-payer replacement key can be
/// created on it (<see cref="WithholdingCertificateGuards"/>). The table follows the shape of <c>Themia.ETax.Withholding</c>'s
/// <c>WithholdingCertificateRecord</c>: id, source event, revision, payer, number, issue date, the four snapshot columns, state and the
/// write-once replacement.
/// </summary>
public sealed partial record WithholdingCertificateTableMap
{
    // PostgreSQL silently truncates identifiers to 63 bytes, and a truncated name no longer matches the literal an existence check compares it
    // with. The longest name derived from the table (the shared guards' "fk_<table>_replaced_same_seller") adds 24 characters, so the table is
    // capped to keep every derived name, including this package's "ux_<table>_source_revision", within 63.
    internal const int MaxIdentifierLength = 63;
    internal const int MaxTableLength = MaxIdentifierLength - 24;

    private static readonly string[] FixedImmutableColumns =
    [
        "document_number", "issue_date", "snapshot_sha256", "snapshot_schema_version", "template_version",
    ];

    /// <summary>The table, unqualified. A table in the connection's <c>search_path</c> needs no schema.</summary>
    public required string Table { get; init; }

    /// <summary>The table's schema. Leave <see langword="null"/> (the default) to follow the connection's <c>search_path</c>.</summary>
    public string? Schema { get; init; }

    /// <summary>The primary key column. Default <c>id</c>.</summary>
    public string IdColumn { get; init; } = "id";

    /// <summary>The column holding the triggering event's id (part of the idempotency key). Default <c>source_event_id</c>.</summary>
    public string SourceEventIdColumn { get; init; } = "source_event_id";

    /// <summary>The revision column (part of the idempotency key). Default <c>revision</c>.</summary>
    public string RevisionColumn { get; init; } = "revision";

    /// <summary>The column holding the SHA-256 of the signature image the certificate was issued with (nullable). Default <c>signature_sha256</c>.</summary>
    public string SignatureSha256Column { get; init; } = "signature_sha256";

    /// <summary>The payer column. Default <c>payer_id</c>.</summary>
    public string PayerIdColumn { get; init; } = "payer_id";

    /// <summary>The lifecycle state column. Default <c>state</c>.</summary>
    public string StateColumn { get; init; } = "state";

    /// <summary>The column holding the replacement's id on a cancelled certificate. Default <c>replaced_by_certificate_id</c>.</summary>
    public string ReplacedByColumn { get; init; } = "replaced_by_certificate_id";

    /// <summary>The snapshot text column. Default <c>snapshot_json</c>.</summary>
    public string SnapshotJsonColumn { get; init; } = "snapshot_json";

    /// <summary>
    /// Columns that may never change once a snapshot exists. When <see langword="null"/> (the default): the id, payer, source event, revision, snapshot and signature-hash
    /// columns as configured here, plus <c>document_number</c>, <c>issue_date</c>, <c>snapshot_sha256</c>, <c>snapshot_schema_version</c> and
    /// <c>template_version</c>. A table that names those differently must list its own columns here, and the
    /// list must contain <see cref="SnapshotJsonColumn"/>.
    /// </summary>
    public IReadOnlyList<string>? ImmutableColumns { get; init; }

    /// <summary>The value stored in <see cref="StateColumn"/> for Issued: an integer, an enum (its NUMBER, so the column must be an integer type) or a plain string of letters, digits and underscores (a text column).</summary>
    public required object IssuedStateValue { get; init; }

    /// <summary>The value stored in <see cref="StateColumn"/> for Cancelled; the same kind as <see cref="IssuedStateValue"/>. The migration checks the column's type and fails if it does not fit.</summary>
    public required object CancelledStateValue { get; init; }

    // An Issued certificate leaves Issued only by being cancelled with a replacement: that is exactly the rule the shared guard enforces
    // for a document in its "sent" (delivered) state, so Issued is passed as that state.
    internal TaxDocumentTableMap ToTaxDocumentTableMap() => new()
    {
        Table = Table,
        Schema = Schema,
        IdColumn = IdColumn,
        SellerIdColumn = PayerIdColumn,
        StateColumn = StateColumn,
        ReplacedByColumn = ReplacedByColumn,
        SnapshotJsonColumn = SnapshotJsonColumn,
        ImmutableColumns = ImmutableColumns ?? [IdColumn, PayerIdColumn, SourceEventIdColumn, RevisionColumn, SnapshotJsonColumn, SignatureSha256Column, .. FixedImmutableColumns],
        SentStateValue = IssuedStateValue,
        CancelledStateValue = CancelledStateValue,
    };

    internal string IssueKeyIndexName => $"ux_{Table}_source_revision";

    internal string QualifiedTable => Schema is null ? Quote(Table) : Quote(Schema) + "." + Quote(Table);

    internal static string Quote(string identifier) => "\"" + identifier + "\"";

    // The shared guards validate everything they use; this validates what only the issue key adds, because its statement is built here.
    internal void ValidateIssueKey()
    {
        CheckIdentifier(Table, nameof(Table), MaxTableLength);
        if (Schema is not null)
            CheckIdentifier(Schema, nameof(Schema), MaxIdentifierLength);
        CheckIdentifier(SourceEventIdColumn, nameof(SourceEventIdColumn), MaxIdentifierLength);
        CheckIdentifier(RevisionColumn, nameof(RevisionColumn), MaxIdentifierLength);
    }

    private static void CheckIdentifier(string value, string paramName, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxLength || !Identifier().IsMatch(value))
        {
            throw new ArgumentException(
                $"'{paramName}' must be a plain identifier (letters, digits, underscore; not starting with a digit; at most {maxLength} characters).",
                paramName);
        }
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex Identifier();
}
