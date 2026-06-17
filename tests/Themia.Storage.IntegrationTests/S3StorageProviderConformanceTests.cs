using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Testcontainers.Minio;
using Themia.Storage;
using Themia.Storage.S3;
using Xunit;

namespace Themia.Storage.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class S3StorageProviderConformanceTests : StorageProviderConformanceTests, IAsyncLifetime
{
    private readonly MinioContainer container = new MinioBuilder("minio/minio:RELEASE.2025-04-22T22-12-26Z").Build();
    private IAmazonS3 client = null!;
    private S3StorageProvider provider = null!;

    protected override IStorageProvider Provider => provider;

    public async Task InitializeAsync()
    {
        await container.StartAsync();

        var credentials = new BasicAWSCredentials(container.GetAccessKey(), container.GetSecretKey());
        var config = new AmazonS3Config
        {
            ServiceURL = container.GetConnectionString(),
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            DefaultAWSCredentials = credentials,
        };
        client = new AmazonS3Client(credentials, config);
        await client.PutBucketAsync(new PutBucketRequest { BucketName = "themia-conf" });
        provider = new S3StorageProvider(client, "themia-conf");
    }

    public async Task DisposeAsync()
    {
        provider.Dispose();
        await container.DisposeAsync();
    }
}
