using Amazon.Runtime;
using Themia.Storage.S3;
using Xunit;

namespace Themia.Storage.Tests;

/// <summary>
/// The SDK's default checksum is a trailer that S3-compatible servers reject on upload: Garage over http, and
/// Cloudflare R2 over https as well (coord #0178). A checksum is requested only where the service requires one,
/// for any custom endpoint; real AWS keeps the SDK default.
/// </summary>
public sealed class S3ChecksumCalculationTests
{
    private static RequestChecksumCalculation Calculation(string? serviceUrl)
    {
        using var client = S3StorageProvider.BuildClient(new S3StorageOptions
        {
            BucketName = "private-bucket",
            ServiceUrl = serviceUrl is null ? null : new Uri(serviceUrl),
            Region = "us-east-1",
            AccessKey = "test-access-key",
            SecretKey = "test-secret-key",
        });
        return client.Config.RequestChecksumCalculation;
    }

    [Theory]
    [InlineData("http://localhost:3900")]
    [InlineData("https://account.r2.cloudflarestorage.com")]
    public void A_custom_endpoint_requests_a_checksum_only_when_required(string serviceUrl)
    {
        Assert.Equal(RequestChecksumCalculation.WHEN_REQUIRED, Calculation(serviceUrl));
    }

    [Fact]
    public void Aws_itself_keeps_the_sdk_default_checksum()
    {
        Assert.Equal(RequestChecksumCalculation.WHEN_SUPPORTED, Calculation(null));
    }
}
