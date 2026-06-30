using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Modules.Export.Jobs;

/// <summary>Establishes ambient tenant context for a background (request-less) code path, restoring the
/// previous value on dispose. The EF tenant query filter reads <see cref="TenantContextAccessor"/>.</summary>
internal static class BackgroundTenantScope
{
    public static IDisposable Begin(TenantId? tenantId)
    {
        var previous = TenantContextAccessor.CurrentTenantId;
        TenantContextAccessor.CurrentTenantId = tenantId;
        return new Restore(() => TenantContextAccessor.CurrentTenantId = previous);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
