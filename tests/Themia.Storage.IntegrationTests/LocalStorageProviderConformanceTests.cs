using Themia.Storage;
using Themia.Storage.Local;
using Xunit;

namespace Themia.Storage.IntegrationTests;

public sealed class LocalStorageProviderConformanceTests : StorageProviderConformanceTests, IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "themia-storage-conf", Guid.NewGuid().ToString("N"));

    protected override IStorageProvider Provider =>
        new LocalStorageProvider(new LocalStorageOptions { RootPath = root, SigningKey = "conf-signing-key-please-change!" });

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
