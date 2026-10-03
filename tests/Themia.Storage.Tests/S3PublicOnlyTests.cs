using Themia.Storage.S3;
using Xunit;

namespace Themia.Storage.Tests;

/// <summary>
/// An S3 slot that serves only public/ keys needs no private bucket (coord #0152). It is an explicit flag, not
/// a loosened check, so a single-provider config that forgets BucketName still fails at boot.
/// </summary>
public sealed class S3PublicOnlyTests
{
    private static S3StorageOptions PublicOnly() => new()
    {
        PublicOnly = true,
        PublicBucketName = "public-bucket",
        PublicBaseUrl = "https://cdn.example.com",
        Region = "us-east-1",
    };

    [Fact]
    public void A_public_only_provider_constructs_with_a_blank_BucketName_and_serves_public_urls()
    {
        using var provider = new S3StorageProvider(PublicOnly());

        Assert.Equal("https://cdn.example.com/listings/1/a.jpg", provider.GetPublicUrl("public/listings/1/a.jpg").ToString());
    }

    [Fact]
    public async Task A_public_only_provider_refuses_a_private_key_on_every_operation_before_any_request()
    {
        using var provider = new S3StorageProvider(PublicOnly());
        var presign = new PresignedUrlRequest(PresignedUrlOperation.Get, TimeSpan.FromMinutes(1));

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.PutAsync("verifications/1/a.pdf", new MemoryStream([1]), new StoragePutOptions("application/pdf")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAsync("verifications/1/a.pdf"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ExistsAsync("verifications/1/a.pdf"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.StatAsync("verifications/1/a.pdf"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.DeleteAsync("verifications/1/a.pdf"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetPresignedUrlAsync("verifications/1/a.pdf", presign));
    }

    [Fact]
    public void Without_the_flag_a_blank_BucketName_still_fails_construction()
    {
        // The guard this flag exists to preserve: not "blank is fine when a public bucket is set".
        var options = PublicOnly();
        options.PublicOnly = false;

        Assert.Throws<ArgumentException>(() => new S3StorageProvider(options));
    }

    [Fact]
    public void A_public_only_config_with_a_BucketName_is_contradictory_and_throws()
    {
        var options = PublicOnly();
        options.BucketName = "private-bucket";

        Assert.Throws<ArgumentException>(() => new S3StorageProvider(options));
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Theory]
    [InlineData("", "https://cdn.example.com")]
    [InlineData("public-bucket", "")]
    public void A_public_only_config_needs_both_the_public_bucket_and_its_base_url(string bucket, string baseUrl)
    {
        var options = PublicOnly();
        options.PublicBucketName = bucket;
        options.PublicBaseUrl = baseUrl;

        Assert.Throws<ArgumentException>(options.Validate);
    }
}
