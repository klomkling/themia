namespace Themia.MultiTenancy;

/// <summary>
/// Marker interface for request types that are exempt from the tenant guard entirely (both the
/// authentication and tenant-presence checks). Implement on commands/queries that legitimately run
/// without a tenant — login, refresh, public, or system/background operations.
/// </summary>
public interface ISkipTenantValidation;
