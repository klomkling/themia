namespace Themia.Framework.Core.Abstractions.Tenancy;

/// <summary>
/// Provides ambient tenant information for scenarios (such as query filters)
/// where the active context must be accessed without capturing per-tenant models.
/// </summary>
public static class TenantContextAccessor
{
    private static readonly AsyncLocal<TenantId?> CurrentTenant = new();

    /// <summary>
    /// Gets or sets the current tenant identifier for the ambient execution context.
    /// </summary>
    public static TenantId? CurrentTenantId
    {
        get => CurrentTenant.Value;
        set => CurrentTenant.Value = value;
    }

    /// <summary>
    /// Switches the ambient tenant for a block and restores the previous value when the returned scope is
    /// disposed. Internal on purpose: a tenant switch is only safe with a tenant read from a row that a secret
    /// has just proved (a refresh-token hash, a one-time-code challenge), which only Themia's own identity
    /// flows establish. Always use it in a <c>using</c> so an exception cannot leak the switch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It switches the ambient accessor, so it only takes effect for data access that reads the tenant from
    /// there: EF Core's runtime tenant filter, and Dapper repositories behind an <see cref="ITenantContext"/>
    /// backed by the accessor (what <c>AddThemiaAspNetCore</c> registers). A custom <see cref="ITenantContext"/>
    /// that returns its own fixed value is not switched.
    /// </para>
    /// <para>
    /// It is internal and used across assemblies through <c>InternalsVisibleTo</c> (Themia.Modules.Identity and
    /// Themia.Modules.Identity.AspNetCore), so those packages and Themia.Framework.Core must be installed at the
    /// same version. All Themia packages share one version, so this holds unless versions are mixed by hand.
    /// </para>
    /// </remarks>
    /// <param name="tenant">The tenant to run as; <see langword="null"/> runs with no tenant.</param>
    internal static IDisposable BeginScope(TenantId? tenant) => new TenantScope(tenant);

    private sealed class TenantScope : IDisposable
    {
        private readonly TenantId? previous;
        private bool disposed;

        public TenantScope(TenantId? tenant)
        {
            previous = CurrentTenant.Value;
            CurrentTenant.Value = tenant;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            CurrentTenant.Value = previous;
        }
    }
}
