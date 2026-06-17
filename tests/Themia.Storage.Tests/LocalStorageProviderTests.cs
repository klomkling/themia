using System.Text;
using Themia.Storage;
using Themia.Storage.Local;
using Xunit;

namespace Themia.Storage.Tests;

public sealed class LocalStorageProviderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "themia-storage-tests", Guid.NewGuid().ToString("N"));

    private LocalStorageProvider NewProvider() =>
        new(new LocalStorageOptions { RootPath = root, SigningKey = "test-signing-key-please-change" });

    private static MemoryStream Bytes(string s) => new(Encoding.UTF8.GetBytes(s));

    [Fact]
    public async Task Put_then_get_round_trips_content_and_type()
    {
        var provider = NewProvider();
        await provider.PutAsync("a/b.txt", Bytes("hello"), new StoragePutOptions("text/plain"));

        var read = await provider.GetAsync("a/b.txt");

        Assert.NotNull(read);
        Assert.Equal("text/plain", read!.ContentType);
        Assert.Equal(5, read.Length);
        using var reader = new StreamReader(read.Content);
        Assert.Equal("hello", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Get_absent_key_returns_null()
    {
        Assert.Null(await NewProvider().GetAsync("missing.txt"));
    }

    [Fact]
    public async Task Exists_reflects_presence()
    {
        var provider = NewProvider();
        Assert.False(await provider.ExistsAsync("x.txt"));
        await provider.PutAsync("x.txt", Bytes("y"), new StoragePutOptions("text/plain"));
        Assert.True(await provider.ExistsAsync("x.txt"));
    }

    [Fact]
    public async Task Delete_is_idempotent()
    {
        var provider = NewProvider();
        await provider.PutAsync("x.txt", Bytes("y"), new StoragePutOptions("text/plain"));
        await provider.DeleteAsync("x.txt");
        await provider.DeleteAsync("x.txt"); // must not throw
        Assert.False(await provider.ExistsAsync("x.txt"));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("a/../../escape.txt")]
    [InlineData("/abs.txt")]
    public async Task Traversal_keys_are_rejected(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => NewProvider().PutAsync(key, Bytes("x"), new StoragePutOptions("text/plain")));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
