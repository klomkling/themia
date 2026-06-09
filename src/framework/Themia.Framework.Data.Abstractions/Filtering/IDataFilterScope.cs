namespace Themia.Framework.Data.Abstractions.Filtering;

/// <summary>
/// Deliberate, explicit opt-out of the tenant filter for the duration of the returned scope.
/// Honoured identically by the EF adapter (IgnoreQueryFilters) and the Dapper tenant-seeded factory.
/// Audit and soft-delete are unaffected.
/// </summary>
public interface IDataFilterScope
{
    /// <summary>Bypasses the tenant filter until the returned scope is disposed.</summary>
    IDisposable BypassTenantFilter();
    /// <summary>True while a bypass scope is active on the current async flow.</summary>
    bool IsTenantFilterBypassed { get; }
}
