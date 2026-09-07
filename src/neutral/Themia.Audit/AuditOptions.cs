using Themia.Audit.Redaction;

namespace Themia.Audit;

/// <summary>
/// Configuration for <c>AddThemiaAudit</c>. <see cref="Engine"/> and <see cref="ConnectionString"/> are
/// validated with <c>ValidateOnStart</c> (design §11) — an adopter supplies both or startup fails.
/// </summary>
public sealed class AuditOptions
{
    /// <summary>
    /// Connection string for the audit store and, unless migration is disabled, the schema migration.
    /// Required; validated non-empty at startup.
    /// </summary>
    /// <remarks>
    /// <b>With <c>Themia.Modules.Audit</c>, this must point at the same database the unit of work writes
    /// to.</b> Under <c>AuditTransactionPolicy.RequireTransaction</c> — the default for activity events —
    /// the row is written on the caller's own connection, against the unqualified table name
    /// <c>themia_audit_events</c>. Only the migration, the dashboard, <c>ITenantAuditReader</c> and the
    /// <c>Never</c> policy use this connection string.
    /// <para>
    /// So pointing this at a separate audit database creates the table there while activity writes go to
    /// the application's database and fail with a missing-table error on the first attempt. Nothing can
    /// detect the mismatch at startup: the two connections are opened by different components at
    /// different times, and a connection string alone does not say which database a unit of work will
    /// later use.
    /// </para>
    /// <para>
    /// A separate audit database is supported only with <c>AuditTransactionPolicy.Never</c> for every
    /// category, which trades away the atomicity guarantee: an activity row then survives a rolled-back
    /// business write, recording something that did not happen.
    /// </para>
    /// </remarks>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// The target database engine. <see cref="AuditEngine.Unspecified"/> (the default) is rejected at
    /// startup so an adopter cannot select a dialect by accident.
    /// </summary>
    public AuditEngine Engine { get; set; }

    /// <summary>Additional sensitive JSON property-name patterns to redact, beyond the built-in defaults.</summary>
    public AuditRedactionOptions Redaction { get; } = new();
}
