namespace Themia.Modules.Audit;

/// <summary>
/// Configuration for <c>AddThemiaAuditModule</c>.
/// </summary>
public sealed class AuditModuleOptions
{
    /// <summary>
    /// The <see cref="AuditTransactionPolicy"/> applied to <c>AuditCategory.Activity</c> entries.
    /// Defaults to <see cref="AuditTransactionPolicy.RequireTransaction"/>; validated with
    /// <see cref="Enum.IsDefined(System.Type,object)"/> at startup, so <see cref="AuditTransactionPolicy.Unspecified"/>
    /// is rejected. <c>AuditCategory.Authentication</c> and <c>AuditCategory.UserLifecycle</c> are always
    /// <see cref="AuditTransactionPolicy.Never"/> and are not configurable through this option.
    /// </summary>
    public AuditTransactionPolicy ActivityPolicy { get; set; } = AuditTransactionPolicy.RequireTransaction;
}
