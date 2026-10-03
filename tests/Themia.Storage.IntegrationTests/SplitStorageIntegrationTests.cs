using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Options;
using Themia.Storage.Local;
using Themia.Storage.S3;
using Themia.Storage.Urls;
using Xunit;

namespace Themia.Storage.IntegrationTests;

/// <summary>
/// The router over real backends (coord #0152): Garage, a real S3-compatible server, as the public slot and the
/// Local provider as the private slot. Proves that a private key never reaches the bucket and that
/// <see cref="IStorageUrlService"/> mints both kinds of link over the same seam, one of which really downloads.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SplitStorageIntegrationTests : IAsyncLifetime
{
    private const ushort S3Port = 3900;
    private const ushort AdminPort = 3903;
    private const string Region = "us-east-1";
    private const string Bucket = "themia-split";
    private const string PublicBaseUrl = "https://cdn.example.com";
    private const string PresignedBaseUrl = "https://api.example.com/api/v1/storage";

    // Garage requires an access key id of "GK" + 24 hex chars and a 64-hex-char secret; throwaway test values.
    private const string AccessKey = "GK0123456789abcdef01234567";
    private const string SecretKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static readonly string GarageToml = $"""
        metadata_dir = "/var/lib/garage/meta"
        data_dir = "/var/lib/garage/data"
        db_engine = "sqlite"
        replication_factor = 1
        rpc_bind_addr = "[::]:3901"
        rpc_public_addr = "127.0.0.1:3901"
        rpc_secret = "0000000000000000000000000000000000000000000000000000000000000000"

        [s3_api]
        s3_region = "{Region}"
        api_bind_addr = "[::]:{S3Port}"

        [admin]
        api_bind_addr = "[::]:{AdminPort}"
        """;

    private readonly string localRoot = Path.Combine(Path.GetTempPath(), "themia-split-it-" + Guid.NewGuid().ToString("N"));

    private readonly IContainer container = new ContainerBuilder("dxflrs/garage:v2.4.0")
        .WithResourceMapping(Encoding.UTF8.GetBytes(GarageToml), FilePath.Of("/etc/garage.toml"))
        .WithEnvironment("GARAGE_DEFAULT_ACCESS_KEY", AccessKey)
        .WithEnvironment("GARAGE_DEFAULT_SECRET_KEY", SecretKey)
        .WithEnvironment("GARAGE_DEFAULT_BUCKET", Bucket)
        .WithCommand("/garage", "server", "--single-node", "--default-bucket")
        .WithPortBinding(S3Port, true)
        .WithPortBinding(AdminPort, true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request.ForPort(AdminPort).ForPath("/health")))
        .Build();

    private AmazonS3Client bucketClient = null!;
    private SplitStorageProvider router = null!;
    private StorageUrlService urls = null!;

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        var serviceUrl = new Uri($"http://{container.Hostname}:{container.GetMappedPublicPort(S3Port)}");

        // A direct client seeds the bucket and inspects it. Garage v2.4 rejects the SDK's default CRC32 trailer on a
        // signed streaming upload over plain HTTP, so checksums are sent only where S3 requires them.
        var credentials = new BasicAWSCredentials(AccessKey, SecretKey);
        bucketClient = new AmazonS3Client(credentials, new AmazonS3Config
        {
            ServiceURL = serviceUrl.AbsoluteUri,
            ForcePathStyle = true,
            AuthenticationRegion = Region,
            DefaultAWSCredentials = credentials,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        });

        var publicSlot = new S3StorageProvider(new S3StorageOptions
        {
            PublicOnly = true,
            PublicBucketName = Bucket,
            PublicBaseUrl = PublicBaseUrl,
            ServiceUrl = serviceUrl,
            ForcePathStyle = true,
            AccessKey = AccessKey,
            SecretKey = SecretKey,
            Region = Region,
        });
        var privateSlot = new LocalStorageProvider(new LocalStorageOptions
        {
            RootPath = localRoot,
            SigningKey = "k-long-enough-for-an-hmac-key-0123",
        });

        router = new SplitStorageProvider(publicSlot, privateSlot);
        urls = new StorageUrlService(router, Options.Create(new StorageUrlOptions { PresignedBaseUrl = PresignedBaseUrl }));
    }

    public async Task DisposeAsync()
    {
        router.Dispose();
        bucketClient.Dispose();
        await container.DisposeAsync();
        if (Directory.Exists(localRoot))
        {
            Directory.Delete(localRoot, recursive: true);
        }
    }

    private async Task SeedPublicObjectAsync(string bucketKey, byte[] bytes)
    {
        using var body = new MemoryStream(bytes);
        await bucketClient.PutObjectAsync(new PutObjectRequest { BucketName = Bucket, Key = bucketKey, InputStream = body });
    }

    private async Task<List<string>> BucketKeysAsync() =>
        (await bucketClient.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket })).S3Objects?.Select(o => o.Key).ToList() ?? [];

    [Fact]
    public async Task A_private_key_is_stored_by_the_Local_slot_and_never_reaches_the_bucket()
    {
        await router.PutAsync("verifications/1/id.pdf", new MemoryStream([9, 9, 9]), new StoragePutOptions("application/pdf"));

        Assert.True(await router.ExistsAsync("verifications/1/id.pdf"));
        Assert.DoesNotContain(await BucketKeysAsync(), key => key.Contains("id.pdf"));
        Assert.True(Directory.EnumerateFiles(localRoot, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task A_public_key_is_read_from_the_bucket_and_the_same_name_without_the_prefix_is_not()
    {
        await SeedPublicObjectAsync("listings/1/a.jpg", [1, 2, 3]);

        var publicRead = await router.GetAsync("public/listings/1/a.jpg");
        var privateRead = await router.GetAsync("listings/1/a.jpg");

        Assert.NotNull(publicRead);
        using (var ms = new MemoryStream())
        {
            await publicRead.Content.CopyToAsync(ms);
            Assert.Equal([1, 2, 3], ms.ToArray());
        }

        Assert.Null(privateRead);
    }

    [Fact]
    public async Task IStorageUrlService_mints_a_local_link_for_a_private_key_and_a_working_S3_link_for_a_public_one()
    {
        await SeedPublicObjectAsync("listings/2/b.jpg", [4, 5, 6]);

        var privateUrl = await urls.GetDownloadUrlAsync("verifications/1/id.pdf", TimeSpan.FromMinutes(5));
        var publicUrl = await urls.GetDownloadUrlAsync("public/listings/2/b.jpg", TimeSpan.FromMinutes(5));

        Assert.StartsWith(PresignedBaseUrl + "/_local/get?", privateUrl.AbsoluteUri);
        // S3StorageProvider does not set GetPreSignedUrlRequest.Protocol, so the SDK signs an https URL even for
        // Garage's plain-HTTP endpoint. Right for S3 and R2; the scheme is not part of the signature, so the test
        // downloads over http. (Pre-existing, unrelated to the router.)
        var expectedStart = $"https://{container.Hostname}:{container.GetMappedPublicPort(S3Port)}/{Bucket}/listings/2/b.jpg?";
        Assert.True(publicUrl.AbsoluteUri.StartsWith(expectedStart, StringComparison.Ordinal), publicUrl.AbsoluteUri);

        using var http = new HttpClient();
        var overHttp = new UriBuilder(publicUrl) { Scheme = Uri.UriSchemeHttp, Port = container.GetMappedPublicPort(S3Port) }.Uri;
        Assert.Equal([4, 5, 6], await http.GetByteArrayAsync(overHttp));
    }

    [Fact]
    public void GetPublicUrl_composes_the_configured_base_with_the_stripped_key()
    {
        Assert.Equal($"{PublicBaseUrl}/listings/1/a.jpg", router.GetPublicUrl("public/listings/1/a.jpg").ToString());
    }
}
