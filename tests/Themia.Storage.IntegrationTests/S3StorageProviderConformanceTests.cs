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

    [Fact]
    public async Task A_provider_built_from_options_writes_and_reads_over_a_plain_http_endpoint()
    {
        // The conformance provider wraps a client this fixture configures itself, which hides what an app gets
        // from S3StorageOptions: the SDK's default checksum is a signed CRC32 trailer on an http upload, which
        // Garage rejects ("Invalid payload signature").
        using var fromOptions = new S3StorageProvider(new S3StorageOptions
        {
            BucketName = GarageFixture.Bucket,
            ServiceUrl = garage.ServiceUrl,
            ForcePathStyle = true,
            AccessKey = GarageFixture.AccessKey,
            SecretKey = GarageFixture.SecretKey,
            Region = GarageFixture.Region,
        });
        var key = $"conf/{Guid.NewGuid():N}.txt";

        await fromOptions.PutAsync(key, new MemoryStream(Encoding.UTF8.GetBytes("from-options")), new StoragePutOptions("text/plain"));

        var read = await fromOptions.GetAsync(key);
        Assert.NotNull(read);
        using var reader = new StreamReader(read.Content);
        Assert.Equal("from-options", await reader.ReadToEndAsync());
    }

    public void Dispose()
    {
        provider?.Dispose();
        client.Dispose();
    }
}
