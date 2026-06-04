using Themia.MultiTenancy.Abstractions;

namespace Themia.MultiTenancy.Internal;

/// <summary>
/// Internal implementation of ITenantAccessor that holds the current tenant for the request scope.
/// This class is registered as Scoped to ensure thread-safety and proper isolation between requests.
/// </summary>
/// <remarks>
/// IMPORTANT: This accessor must be registered with Scoped lifetime and should only be used
/// within the context of an HTTP request. The TenantResolutionMiddleware sets the Current
/// property at the beginning of each request.
/// </remarks>
internal sealed class TenantAccessor : ITenantAccessor
{
    /// <summary>
    /// Gets or sets the current tenant for this request scope.
    /// </summary>
    public TenantInfo? Current { get; set; }
}
