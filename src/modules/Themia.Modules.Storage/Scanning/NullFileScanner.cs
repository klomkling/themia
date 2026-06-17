namespace Themia.Modules.Storage.Scanning;

/// <summary>A pass-through <see cref="IFileScanner"/> (always clean). The default until a real scanner
/// (ClamAV) is registered in 0.5.4.</summary>
public sealed class NullFileScanner : IFileScanner
{
    /// <inheritdoc />
    public Task<FileScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default) =>
        Task.FromResult(FileScanResult.Clean);
}
