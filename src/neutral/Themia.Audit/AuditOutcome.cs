namespace Themia.Audit;

/// <summary>
/// The result of the event described by an <see cref="AuditEntry"/>.
/// </summary>
/// <remarks>
/// <see cref="Unspecified"/> reserves the enum's default value (<c>0</c>) so an entry whose outcome
/// was never set fails <see cref="AuditEntry.Validate"/> instead of silently reading as a real
/// outcome. See <see cref="AuditCategory"/> for the defect this pattern corrects.
/// </remarks>
public enum AuditOutcome
{
    /// <summary>Not set. Always rejected by <see cref="AuditEntry.Validate"/>.</summary>
    Unspecified = 0,

    /// <summary>The action completed as intended.</summary>
    Success,

    /// <summary>The action was attempted and failed.</summary>
    Failure,

    /// <summary>The action was refused by policy or authorization, distinct from a failure.</summary>
    Denied,
}
