using Themia.Storage.S3;
using Xunit;

namespace Themia.Storage.Tests;

/// <summary>
/// A presigned URL must use the scheme of the endpoint it was signed for. The AWS SDK signs https unless told
/// otherwise, so a provider pointed at a plain-http MinIO or Garage handed out URLs that cannot connect.
/// </summary>
public sealed class S3PresignedUrlSchemeTests
{
    private static readonly PresignedUrlRequest Get = new(PresignedUrlOperation.Get, TimeSpan.FromMinutes(5));
    private static readonly PresignedUrlRequest Put = new(PresignedUrlOperation.Put, TimeSpan.FromMinutes(5), "image/png");

    private static S3StorageProvider Create(string? serviceUrl) => new(new S3StorageOptions
    {
        BucketName = "private-bucket",
        ServiceUrl = serviceUrl is null ? null : new Uri(serviceUrl),
        ForcePathStyle = serviceUrl is not null,
        Region = "us-east-1",
        AccessKey = "test-access-key",
        SecretKey = "test-secret-key",
    });

    [Theory]
    [InlineData("http://localhost:9000", "http")]
    [InlineData("HTTP://localhost:9000", "http")]
    [InlineData("https://acct.r2.cloudflarestorage.com", "https")]
    [InlineData(null, "https")]
    public async Task A_presigned_get_url_uses_the_scheme_of_the_endpoint(string? serviceUrl, string expectedScheme)
    {
        using var provider = Create(serviceUrl);

        var url = await provider.GetPresignedUrlAsync("a.txt", Get);

        Assert.Equal(expectedScheme, url.Scheme);
    }

    [Fact]
    public async Task A_presigned_put_url_uses_the_scheme_of_the_endpoint_too()
    {
        using var provider = Create("http://localhost:9000");

        var url = await provider.GetPresignedUrlAsync("a.png", Put);

        Assert.Equal("http", url.Scheme);
    }

    [Fact]
    public async Task A_public_key_presigns_against_the_public_bucket_with_the_scheme_of_the_endpoint()
    {
        // Public keys presign against the public bucket; the scheme rule is the same.
        using var provider = new S3StorageProvider(new S3StorageOptions
        {
            BucketName = "private-bucket",
            PublicBucketName = "public-bucket",
            PublicBaseUrl = "https://cdn.example.com",
            ServiceUrl = new Uri("http://localhost:9000"),
            ForcePathStyle = true,
            Region = "us-east-1",
            AccessKey = "test-access-key",
            SecretKey = "test-secret-key",
        });

        var url = await provider.GetPresignedUrlAsync("public/a.jpg", Get);

        Assert.Equal("http", url.Scheme);
        Assert.Contains("/public-bucket/", url.AbsolutePath);
    }
}
