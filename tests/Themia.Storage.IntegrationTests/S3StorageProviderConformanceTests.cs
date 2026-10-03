using System.Text;
using Amazon.S3;
using Themia.Storage;
using Themia.Storage.S3;
using Xunit;

namespace Themia.Storage.IntegrationTests;

/// <summary>Runs the conformance suite against Garage, a real S3-compatible server.</summary>
[Trait("Category", "Integration")]
public sealed class S3StorageProviderConformanceTests(GarageFixture garage)
    : StorageProviderConformanceTests, IClassFixture<GarageFixture>, IDisposable
{
    private readonly AmazonS3Client client = garage.CreateClient();
    private S3StorageProvider? provider;

    protected override IStorageProvider Provider => provider ??= new S3StorageProvider(client, GarageFixture.Bucket);

    [Fact]
    public async Task Presigned_get_url_downloads_the_object_from_a_plain_http_endpoint()
    {
        // The SDK signs https unless told otherwise; Garage listens on plain http, so an https URL cannot connect.
        var key = $"conf/{Guid.NewGuid():N}.txt";
        await Provider.PutAsync(key, new MemoryStream(Encoding.UTF8.GetBytes("presigned")), new StoragePutOptions("text/plain"));
        var url = await Provider.GetPresignedUrlAsync(key, new PresignedUrlRequest(PresignedUrlOperation.Get, TimeSpan.FromMinutes(5)));

        using var http = new HttpClient();

        Assert.Equal("http", url.Scheme);
        Assert.Equal("presigned", await http.GetStringAsync(url));
    }

    public void Dispose()
    {
        provider?.Dispose();
        client.Dispose();
    }
}
