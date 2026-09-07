namespace Themia.Audit;

/// <summary>
/// Broad classification of the event described by an <see cref="AuditEntry"/>.
/// </summary>
/// <remarks>
/// <see cref="Unspecified"/> reserves the enum's default value (<c>0</c>) so an entry whose category
/// was never set fails <see cref="AuditEntry.Validate"/> instead of silently reading as a real
/// category. Direct correction of <c>SequenceEngine.Postgres = 0</c> in 0.22.0, where the default
/// value was itself a legitimate value and an unset field passed <see cref="System.Enum.IsDefined"/>.
/// </remarks>
public enum AuditCategory
{
    /// <summary>Not set. Always rejected by <see cref="AuditEntry.Validate"/>.</summary>
    Unspecified = 0,

    /// <summary>A business action the adopter performed, e.g. <c>PROPOSAL_ACCEPTED</c>.</summary>
    Activity,

    /// <summary>A login, logout, or token lifecycle event.</summary>
    Authentication,

    /// <summary>Account creation, deactivation, or another identity lifecycle event.</summary>
    UserLifecycle,
}
