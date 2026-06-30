namespace Themia.Framework.Data.Abstractions.Filtering;

/// <summary>AsyncLocal-backed <see cref="IDataFilterScope"/>. The single shared filter-bypass carrier
/// for both the tenant and soft-delete axes.</summary>
public sealed class DataFilterScope : IDataFilterScope
{
    private static readonly AsyncLocal<bool> TenantBypassed = new();
    private static readonly AsyncLocal<bool> SoftDeleteBypassed = new();

    /// <inheritdoc />
    public bool IsTenantFilterBypassed => TenantBypassed.Value;

    /// <inheritdoc />
    public bool IsSoftDeleteFilterBypassed => SoftDeleteBypassed.Value;

    /// <summary>The ambient soft-delete bypass flag, read by the EF query filter expression at query time.
    /// Internal because EF query filters compiled in <c>OnModelCreating</c> cannot capture a DI instance, so
    /// the flag is exposed statically to the EFCore assembly only (via InternalsVisibleTo) — not as public API.</summary>
    internal static bool SoftDeleteBypassedAmbient => SoftDeleteBypassed.Value;

    /// <inheritdoc />
    public IDisposable BypassTenantFilter()
    {
        var previous = TenantBypassed.Value;
        TenantBypassed.Value = true;
        return new Restore(() => TenantBypassed.Value = previous);
    }

    /// <inheritdoc />
    public IDisposable BypassSoftDeleteFilter()
    {
        var previous = SoftDeleteBypassed.Value;
        SoftDeleteBypassed.Value = true;
        return new Restore(() => SoftDeleteBypassed.Value = previous);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
