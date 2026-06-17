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

        // AWS SDK for .NET v4's synchronous GetPreSignedURL resolves credentials through the
        // default identity chain rather than the client-attached credentials, so the MinIO root
        // credentials are exported as env vars to make that chain succeed under the test process.
        Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", container.GetAccessKey());
        Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", container.GetSecretKey());

        var config = new AmazonS3Config
        {
            ServiceURL = container.GetConnectionString(),
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
        };
        client = new AmazonS3Client(new BasicAWSCredentials(container.GetAccessKey(), container.GetSecretKey()), config);
        await client.PutBucketAsync(new PutBucketRequest { BucketName = "themia-conf" });
        provider = new S3StorageProvider(client, "themia-conf");
    }

    public async Task DisposeAsync()
    {
        provider.Dispose();
        await container.DisposeAsync();
    }
}
