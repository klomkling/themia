using Themia.Storage.S3;
using Xunit;

namespace Themia.Storage.Tests;

/// <summary>
/// A custom endpoint still signs with a region, and an S3-compatible server such as Garage rejects a signature
/// whose region is not the one it is configured with (coord #0154). The region is read back from the credential
/// scope of a presigned URL, the one place it is observable without a live server.
/// </summary>
public sealed class S3CustomEndpointRegionTests
{
    private static readonly PresignedUrlRequest Get = new(PresignedUrlOperation.Get, TimeSpan.FromMinutes(5));

    private static async Task<string> SigningRegionAsync(string? region)
    {
        using var provider = new S3StorageProvider(new S3StorageOptions
        {
            BucketName = "private-bucket",
            ServiceUrl = new Uri("http://localhost:3900"),
            ForcePathStyle = true,
            Region = region,
            AccessKey = "test-access-key",
            SecretKey = "test-secret-key",
        });

        var url = await provider.GetPresignedUrlAsync("a.txt", Get);

        // X-Amz-Credential=<access key>/<date>/<region>/s3/aws4_request
        var credential = Uri.UnescapeDataString(
            System.Web.HttpUtility.ParseQueryString(url.Query)["X-Amz-Credential"]!);
        return credential.Split('/')[2];
    }

    [Fact]
    public async Task A_custom_endpoint_signs_with_the_configured_region()
    {
        Assert.Equal("garage", await SigningRegionAsync("garage"));
    }

    [Fact]
    public async Task A_custom_endpoint_with_no_region_keeps_the_sdk_default()
    {
        Assert.Equal("us-east-1", await SigningRegionAsync(null));
    }

    [Fact]
    public async Task A_custom_endpoint_with_a_blank_region_keeps_the_sdk_default()
    {
        Assert.Equal("us-east-1", await SigningRegionAsync("  "));
    }
}
