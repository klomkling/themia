using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Themia.Storage.IntegrationTests;

/// <summary>One Garage container (a real S3-compatible server) shared by every test in a class. MinIO was the server
/// here until its images stopped being publicly pullable (quay.io and Docker Hub both answer 401 to anonymous pulls).
/// Tests share the bucket, so each must use keys of its own.</summary>
public sealed class GarageFixture : IAsyncLifetime
{
    public const string Region = "us-east-1";
    public const string Bucket = "themia-conf";

    // Garage requires an access key id of "GK" + 24 hex chars and a 64-hex-char secret; throwaway test values.
    public const string AccessKey = "GK0123456789abcdef01234567";
    public const string SecretKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const ushort S3Port = 3900;
    private const ushort AdminPort = 3903;

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

    public Uri ServiceUrl => new($"http://{container.Hostname}:{container.GetMappedPublicPort(S3Port)}");

    public Task InitializeAsync() => container.StartAsync();

    public async Task DisposeAsync() => await container.DisposeAsync();

    /// <summary>A client the caller disposes. Garage v2.4 rejects the SDK's default CRC32 trailer on a signed streaming
    /// upload over plain HTTP ("Invalid payload signature"), so checksums are sent only where S3 requires them.</summary>
    public AmazonS3Client CreateClient()
    {
        var credentials = new BasicAWSCredentials(AccessKey, SecretKey);
        return new AmazonS3Client(credentials, new AmazonS3Config
        {
            ServiceURL = ServiceUrl.AbsoluteUri,
            ForcePathStyle = true,
            AuthenticationRegion = Region,
            DefaultAWSCredentials = credentials,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        });
    }
}
