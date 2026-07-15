using Themia.Storage.S3;
using Xunit;

namespace Themia.Storage.Tests;

public sealed class S3PublicContainerTests
{
    private static S3StorageProvider Create() => new(new S3StorageOptions
    {
        BucketName = "private-bucket",
        PublicBucketName = "public-bucket",
        PublicBaseUrl = "https://cdn.example.com",
        Region = "us-east-1",
    });

    [Fact]
    public void GetPublicUrl_composes_the_configured_base_with_the_stripped_key()
    {
        Assert.Equal("https://cdn.example.com/t1/a.jpg", Create().GetPublicUrl("public/t1/a.jpg").ToString());
    }

    [Fact]
    public void GetPublicUrl_throws_for_a_private_key()
    {
        Assert.Throws<InvalidOperationException>(() => Create().GetPublicUrl("t1/a.jpg"));
    }

    [Fact]
    public void GetPublicUrl_throws_when_no_public_bucket_is_configured()
    {
        var provider = new S3StorageProvider(new S3StorageOptions { BucketName = "private-bucket", Region = "us-east-1" });
        Assert.Throws<InvalidOperationException>(() => provider.GetPublicUrl("public/t1/a.jpg"));
    }

    [Fact]
    public void Validate_rejects_a_relative_PublicBaseUrl()
    {
        // Uri.TryCreate(..., Absolute) parses "/media" as file:///media on Unix — Validate must still reject it.
        var options = new S3StorageOptions
        {
            BucketName = "private-bucket",
            PublicBucketName = "public-bucket",
            PublicBaseUrl = "/media",
        };
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void Validate_rejects_a_public_bucket_without_a_base_url()
    {
        var options = new S3StorageOptions { BucketName = "private-bucket", PublicBucketName = "public-bucket" };
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void Validate_rejects_a_base_url_without_a_public_bucket()
    {
        var options = new S3StorageOptions { BucketName = "private-bucket", PublicBaseUrl = "https://cdn.example.com" };
        Assert.Throws<ArgumentException>(options.Validate);
    }
}
