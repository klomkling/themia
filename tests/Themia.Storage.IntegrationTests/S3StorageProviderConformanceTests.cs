using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Themia.Storage;
using Themia.Storage.S3;
using Xunit;

namespace Themia.Storage.IntegrationTests;

/// <summary>Runs the conformance suite against Garage, a real S3-compatible server. MinIO was the server here
/// until its images stopped being publicly pullable (quay.io and Docker Hub both answer 401 to anonymous pulls).</summary>
[Trait("Category", "Integration")]
public sealed class S3StorageProviderConformanceTests : StorageProviderConformanceTests, IAsyncLifetime
{
    private const ushort S3Port = 3900;
    private const ushort AdminPort = 3903;
    private const string Region = "us-east-1";
    private const string Bucket = "themia-conf";

    // Garage requires an access key id of "GK" + 24 hex chars and a 64-hex-char secret; throwaway test values.
    private const string AccessKey = "GK0123456789abcdef01234567";
    private const string SecretKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    // Single node, no replication. The S3 region must match the client's AuthenticationRegion or SigV4 fails.
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

    // --single-node lays out the cluster and --default-bucket creates the key + bucket from the env vars, both
    // before the servers start listening, so a healthy admin endpoint means the bucket is ready.
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

    private S3StorageProvider provider = null!;

    protected override IStorageProvider Provider => provider;

    public async Task InitializeAsync()
    {
        await container.StartAsync();

        var credentials = new BasicAWSCredentials(AccessKey, SecretKey);
        var config = new AmazonS3Config
        {
            ServiceURL = $"http://{container.Hostname}:{container.GetMappedPublicPort(S3Port)}",
            ForcePathStyle = true,
            AuthenticationRegion = Region,
            DefaultAWSCredentials = credentials,
            // AWSSDK v4 defaults to a CRC32 trailer on a signed streaming upload, which Garage v2.4 rejects
            // ("Invalid payload signature") on PutObject over plain HTTP. Send checksums only where S3 requires them.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        };
        provider = new S3StorageProvider(new AmazonS3Client(credentials, config), Bucket);
    }

    [Fact]
    public async Task Presigned_get_url_downloads_the_object_from_a_plain_http_endpoint()
    {
        // The SDK signs https unless told otherwise; Garage listens on plain http, so an https URL cannot connect.
        var key = $"conf/{Guid.NewGuid():N}.txt";
        await provider.PutAsync(key, new MemoryStream(Encoding.UTF8.GetBytes("presigned")), new StoragePutOptions("text/plain"));
        var url = await provider.GetPresignedUrlAsync(key, new PresignedUrlRequest(PresignedUrlOperation.Get, TimeSpan.FromMinutes(5)));

        using var http = new HttpClient();

        Assert.Equal("http", url.Scheme);
        Assert.Equal("presigned", await http.GetStringAsync(url));
    }

    [Fact]
    public async Task A_provider_built_from_options_writes_and_reads_over_a_plain_http_endpoint()
    {
        // The conformance provider above wraps a client this test configured itself, which hides what an app gets
        // from S3StorageOptions: the SDK's default checksum is a signed CRC32 trailer on an http upload, which
        // Garage rejects ("Invalid payload signature").
        using var fromOptions = new S3StorageProvider(new S3StorageOptions
        {
            BucketName = Bucket,
            ServiceUrl = new Uri($"http://{container.Hostname}:{container.GetMappedPublicPort(S3Port)}"),
            ForcePathStyle = true,
            AccessKey = AccessKey,
            SecretKey = SecretKey,
            Region = Region,
        });
        var key = $"conf/{Guid.NewGuid():N}.txt";

        await fromOptions.PutAsync(key, new MemoryStream(Encoding.UTF8.GetBytes("from-options")), new StoragePutOptions("text/plain"));

        var read = await fromOptions.GetAsync(key);
        Assert.NotNull(read);
        using var reader = new StreamReader(read.Content);
        Assert.Equal("from-options", await reader.ReadToEndAsync());
    }

    public async Task DisposeAsync()
    {
        provider.Dispose();
        await container.DisposeAsync();
    }
}
