using System.Text;
using Themia.Storage;
using Xunit;

namespace Themia.Storage.IntegrationTests;

public abstract class StorageProviderConformanceTests
{
    protected abstract IStorageProvider Provider { get; }

    private static MemoryStream Bytes(string s) => new(Encoding.UTF8.GetBytes(s));

    [Fact]
    public async Task Put_then_get_round_trips()
    {
        var key = $"conf/{Guid.NewGuid():N}.txt";
        await Provider.PutAsync(key, Bytes("hello"), new StoragePutOptions("text/plain"));

        var read = await Provider.GetAsync(key);
        Assert.NotNull(read);
        using var reader = new StreamReader(read!.Content);
        Assert.Equal("hello", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Get_absent_returns_null() =>
        Assert.Null(await Provider.GetAsync($"conf/{Guid.NewGuid():N}.txt"));

    [Fact]
    public async Task Exists_and_delete()
    {
        var key = $"conf/{Guid.NewGuid():N}.txt";
        await Provider.PutAsync(key, Bytes("x"), new StoragePutOptions("text/plain"));
        Assert.True(await Provider.ExistsAsync(key));
        await Provider.DeleteAsync(key);
        Assert.False(await Provider.ExistsAsync(key));
    }

    [Fact]
    public async Task Presigned_get_url_is_returned()
    {
        var key = $"conf/{Guid.NewGuid():N}.txt";
        await Provider.PutAsync(key, Bytes("x"), new StoragePutOptions("text/plain"));
        var url = await Provider.GetPresignedUrlAsync(key, new PresignedUrlRequest(PresignedUrlOperation.Get, TimeSpan.FromMinutes(5)));
        Assert.False(string.IsNullOrWhiteSpace(url.ToString()));
    }
}
