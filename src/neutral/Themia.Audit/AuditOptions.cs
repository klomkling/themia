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
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// The target database engine. <see cref="AuditEngine.Unspecified"/> (the default) is rejected at
    /// startup so an adopter cannot select a dialect by accident.
    /// </summary>
    public AuditEngine Engine { get; set; }

    /// <summary>Additional sensitive JSON property-name patterns to redact, beyond the built-in defaults.</summary>
    public AuditRedactionOptions Redaction { get; } = new();
}
