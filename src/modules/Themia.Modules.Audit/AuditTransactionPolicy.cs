namespace Themia.Modules.Audit;

/// <summary>
/// How <see cref="TransactionalAuditRecorder"/> relates an audit write to the caller's ambient
/// transaction (design §7). Lives in this module, not the neutral <c>Themia.Audit</c> core, because the
/// neutral core has no concept of an ambient transaction.
/// </summary>
/// <remarks>
/// <see cref="Unspecified"/> reserves the enum's default value (<c>0</c>), for the same reason as
/// <c>Themia.Audit.AuditCategory.Unspecified</c> — an unset policy must fail validation rather than
/// silently read as a real one.
/// </remarks>
public enum AuditTransactionPolicy
{
    /// <summary>Not set. Rejected by <c>AddThemiaAuditModule</c>'s options validation.</summary>
    Unspecified = 0,

    /// <summary>
    /// Requires an ambient transaction; <see cref="TransactionalAuditRecorder.RecordAsync"/> throws when
    /// none is open, naming <c>IUnitOfWork.ExecuteInTransactionAsync</c> as the fix. The default for
    /// <c>AuditCategory.Activity</c> — the guarantee that the audit row commits or rolls back with the
    /// business write is real, or its absence is loud.
    /// </summary>
    RequireTransaction,

    /// <summary>
    /// Enlists in the ambient transaction when one is open; otherwise writes durably on its own
    /// connection without throwing. Weaker than <see cref="RequireTransaction"/> — an adopter who accepts
    /// that a row can survive a rollback of a transaction it never joined may opt into this.
    /// </summary>
    JoinIfPresent,

    /// <summary>
    /// Always writes on its own connection, ignoring any ambient transaction. Hardwired (not
    /// configurable) for <c>AuditCategory.Authentication</c> and <c>AuditCategory.UserLifecycle</c>: a
    /// failed login has no transaction to join, and must be recorded even when the surrounding request
    /// rolls back.
    /// </summary>
    Never,
}
