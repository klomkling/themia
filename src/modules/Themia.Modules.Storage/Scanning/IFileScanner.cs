namespace Themia.Modules.Storage.Scanning;

/// <summary>The result of scanning an upload.</summary>
/// <param name="IsClean">Whether the content is clean.</param>
/// <param name="Threat">The threat name, when not clean.</param>
public readonly record struct FileScanResult(bool IsClean, string? Threat)
{
    /// <summary>A clean result.</summary>
    public static FileScanResult Clean { get; } = new(true, null);
}

/// <summary>Scans upload content for malware before it is stored. The default is a no-op; a ClamAV
/// implementation arrives in 0.5.4 (Themia.Storage.ClamAV).</summary>
public interface IFileScanner
{
    /// <summary>Scans <paramref name="content"/>. The stream must be readable; implementations restore its
    /// position if they consume it.</summary>
    /// <param name="content">The content to scan.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The scan result.</returns>
    Task<FileScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default);
}
