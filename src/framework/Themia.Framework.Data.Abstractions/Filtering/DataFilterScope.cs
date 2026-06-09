namespace Themia.Framework.Data.Abstractions.Filtering;

/// <summary>AsyncLocal-backed <see cref="IDataFilterScope"/>. The single shared tenant-filter-bypass carrier.</summary>
public sealed class DataFilterScope : IDataFilterScope
{
    private static readonly AsyncLocal<bool> Bypassed = new();

    /// <inheritdoc />
    public bool IsTenantFilterBypassed => Bypassed.Value;

    /// <inheritdoc />
    public IDisposable BypassTenantFilter()
    {
        var previous = Bypassed.Value;
        Bypassed.Value = true;
        return new Restore(() => Bypassed.Value = previous);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
