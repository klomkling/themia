namespace Themia.Storage.Tests;

/// <summary>A slot that records what it was asked to do and never touches storage.</summary>
internal sealed class SpyStorageProvider(string name, bool hasPublicContainer = false) : IStorageProvider, IDisposable
{
    public List<string> Calls { get; } = [];

    public int DisposeCount { get; private set; }

    public Task<StorageObjectInfo> PutAsync(string key, Stream content, StoragePutOptions options, CancellationToken cancellationToken = default)
    {
        Calls.Add($"Put:{key}");
        return Task.FromResult(new StorageObjectInfo(key, 0, options.ContentType, null));
    }

    public Task<StorageReadResult?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        Calls.Add($"Get:{key}");
        return Task.FromResult<StorageReadResult?>(null);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        Calls.Add($"Exists:{key}");
        return Task.FromResult(false);
    }

    public Task<StorageObjectInfo?> StatAsync(string key, CancellationToken cancellationToken = default)
    {
        Calls.Add($"Stat:{key}");
        return Task.FromResult<StorageObjectInfo?>(null);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        Calls.Add($"Delete:{key}");
        return Task.CompletedTask;
    }

    public Task<Uri> GetPresignedUrlAsync(string key, PresignedUrlRequest request, CancellationToken cancellationToken = default)
    {
        Calls.Add($"Presign:{key}");
        return Task.FromResult(new Uri($"https://{name}.example.com/{key}?sig=1"));
    }

    public Uri GetPublicUrl(string key)
    {
        Calls.Add($"PublicUrl:{key}");
        if (!hasPublicContainer || !StorageKey.IsPublic(key))
        {
            throw new InvalidOperationException($"{name} has no public container for '{key}'.");
        }

        return new Uri($"https://{name}.example.com/{StorageKey.StripVisibilityPrefix(key)}");
    }

    public void Dispose() => DisposeCount++;
}
