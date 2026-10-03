using Themia.Storage.Local;
using Themia.Storage.S3;
using Xunit;

namespace Themia.Storage.Tests;

/// <summary>
/// The router over a public and a private slot (coord #0152). The key decides the slot; the declared
/// Visibility only cross-checks a write; the key is normalised before it is classified. The "wrong side"
/// cases are adapted from ezy-assets #399, with key-form inputs.
/// </summary>
public sealed class SplitStorageProviderTests
{
    private readonly SpyStorageProvider publicSlot = new("public", hasPublicContainer: true);
    private readonly SpyStorageProvider privateSlot = new("private");

    private SplitStorageProvider Create()
    {
        var provider = new SplitStorageProvider(publicSlot, privateSlot);

        // Construction probes the public slot; the tests below are about what happens afterwards.
        publicSlot.Calls.Clear();
        return provider;
    }

    private static readonly StoragePutOptions PublicWrite = new("image/jpeg", Visibility: StorageVisibility.Public);
    private static readonly StoragePutOptions PrivateWrite = new("application/pdf");

    [Fact]
    public async Task A_public_key_reaches_only_the_public_slot_on_every_operation()
    {
        var provider = Create();
        var presign = new PresignedUrlRequest(PresignedUrlOperation.Get, TimeSpan.FromMinutes(1));

        await provider.PutAsync("public/a.jpg", new MemoryStream([1]), PublicWrite);
        await provider.GetAsync("public/a.jpg");
        await provider.ExistsAsync("public/a.jpg");
        await provider.StatAsync("public/a.jpg");
        await provider.DeleteAsync("public/a.jpg");
        await provider.GetPresignedUrlAsync("public/a.jpg", presign);

        Assert.Equal(
            ["Put:public/a.jpg", "Get:public/a.jpg", "Exists:public/a.jpg", "Stat:public/a.jpg", "Delete:public/a.jpg", "Presign:public/a.jpg"],
            publicSlot.Calls);
        Assert.Empty(privateSlot.Calls);
    }

    // Anything that is not the whole first segment "public/" is private: it fails closed.
    [Theory]
    [InlineData("x")]
    [InlineData("verifications/1/a.pdf")]
    [InlineData("private/x")]
    [InlineData("private/x/public")]
    [InlineData("publicity/x")]
    [InlineData("Public/x")]
    public async Task A_key_that_is_not_public_reaches_only_the_private_slot_on_every_operation(string key)
    {
        var provider = Create();
        var presign = new PresignedUrlRequest(PresignedUrlOperation.Get, TimeSpan.FromMinutes(1));

        await provider.PutAsync(key, new MemoryStream([1]), PrivateWrite);
        await provider.GetAsync(key);
        await provider.ExistsAsync(key);
        await provider.StatAsync(key);
        await provider.DeleteAsync(key);
        await provider.GetPresignedUrlAsync(key, presign);

        Assert.Equal(
            [$"Put:{key}", $"Get:{key}", $"Exists:{key}", $"Stat:{key}", $"Delete:{key}", $"Presign:{key}"],
            privateSlot.Calls);
        Assert.Empty(publicSlot.Calls);
    }

    [Theory]
    [InlineData("public/a.jpg", StorageVisibility.Private)]
    [InlineData("a.jpg", StorageVisibility.Public)]
    public async Task A_write_whose_key_and_Visibility_disagree_throws_and_reaches_neither_slot(string key, StorageVisibility visibility)
    {
        // Routing on Visibility alone would store the object and leave it unreachable by its own key.
        var provider = Create();

        await Assert.ThrowsAsync<ArgumentException>(() => provider.PutAsync(
            key, new MemoryStream([1]), new StoragePutOptions("image/jpeg", Visibility: visibility)));

        Assert.Empty(publicSlot.Calls);
        Assert.Empty(privateSlot.Calls);
    }

    [Fact]
    public async Task A_backslash_is_normalised_before_the_key_is_classified_and_the_slot_gets_the_normalised_key()
    {
        var provider = Create();

        await provider.PutAsync(@"public\a.jpg", new MemoryStream([1]), PublicWrite);

        Assert.Equal(["Put:public/a.jpg"], publicSlot.Calls);
        Assert.Empty(privateSlot.Calls);
    }

    [Theory]
    [InlineData("public/../x")]
    [InlineData("/public/x")]
    [InlineData("a/../b")]
    [InlineData("")]
    public async Task A_key_that_normalisation_rejects_reaches_neither_slot_on_every_operation(string key)
    {
        // "public/../x" starts with "public/": classified raw, it would go to the public slot.
        var provider = Create();
        var presign = new PresignedUrlRequest(PresignedUrlOperation.Get, TimeSpan.FromMinutes(1));

        await Assert.ThrowsAnyAsync<ArgumentException>(() => provider.GetAsync(key));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => provider.ExistsAsync(key));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => provider.StatAsync(key));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => provider.DeleteAsync(key));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => provider.GetPresignedUrlAsync(key, presign));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => provider.PutAsync(key, new MemoryStream([1]), PrivateWrite));

        Assert.Empty(publicSlot.Calls);
        Assert.Empty(privateSlot.Calls);
    }

    [Fact]
    public void GetPublicUrl_is_answered_by_the_public_slot_for_a_public_key()
    {
        var provider = Create();

        Assert.Equal("https://public.example.com/a.jpg", provider.GetPublicUrl("public/a.jpg").ToString());
        Assert.Equal(["PublicUrl:public/a.jpg"], publicSlot.Calls);
    }

    [Fact]
    public void GetPublicUrl_throws_for_a_private_key_without_consulting_either_slot()
    {
        var provider = Create();

        Assert.Throws<InvalidOperationException>(() => provider.GetPublicUrl("verifications/1/a.pdf"));

        Assert.Empty(publicSlot.Calls);
        Assert.Empty(privateSlot.Calls);
    }

    [Fact]
    public void Construction_refuses_a_null_slot()
    {
        Assert.Throws<ArgumentNullException>(() => new SplitStorageProvider(null!, privateSlot));
        Assert.Throws<ArgumentNullException>(() => new SplitStorageProvider(publicSlot, null!));
    }

    [Fact]
    public void Construction_refuses_the_same_instance_in_both_slots()
    {
        Assert.Throws<ArgumentException>(() => new SplitStorageProvider(publicSlot, publicSlot));
    }

    [Fact]
    public void Construction_refuses_a_public_slot_with_no_public_container_and_says_so()
    {
        var noPublicContainer = new SpyStorageProvider("nopublic", hasPublicContainer: false);

        var ex = Assert.Throws<InvalidOperationException>(() => new SplitStorageProvider(noPublicContainer, privateSlot));

        Assert.Contains("public container", ex.Message);
    }

    // The probe assumes a real provider with no public container throws InvalidOperationException from
    // GetPublicUrl. The spy throws by construction, so these run the assumption against the real providers.
    [Fact]
    public void Construction_refuses_a_real_Local_public_slot_with_no_public_container()
    {
        var local = new LocalStorageProvider(new LocalStorageOptions
        {
            RootPath = Path.Combine(Path.GetTempPath(), "themia-split-probe-" + Guid.NewGuid().ToString("N")),
            SigningKey = "k-long-enough-for-an-hmac-key-0123",
        });

        var ex = Assert.Throws<InvalidOperationException>(() => new SplitStorageProvider(local, privateSlot));

        Assert.Contains("public container", ex.Message);
    }

    [Fact]
    public void Construction_refuses_a_real_S3_public_slot_with_no_public_container()
    {
        using var s3 = new S3StorageProvider(new S3StorageOptions { BucketName = "private-bucket", Region = "us-east-1" });

        var ex = Assert.Throws<InvalidOperationException>(() => new SplitStorageProvider(s3, privateSlot));

        Assert.Contains("public container", ex.Message);
    }

    [Fact]
    public void Construction_accepts_real_public_slots_that_have_a_public_container()
    {
        using var s3 = new S3StorageProvider(new S3StorageOptions
        {
            PublicOnly = true,
            PublicBucketName = "public-bucket",
            PublicBaseUrl = "https://cdn.example.com",
            Region = "us-east-1",
        });
        var root = Path.Combine(Path.GetTempPath(), "themia-split-probe-" + Guid.NewGuid().ToString("N"));
        var local = new LocalStorageProvider(new LocalStorageOptions
        {
            RootPath = root,
            SigningKey = "k-long-enough-for-an-hmac-key-0123",
            PublicRootPath = Path.Combine(root, "public"),
            PublicBaseUrl = "https://cdn.example.com",
        });

        _ = new SplitStorageProvider(s3, privateSlot);
        _ = new SplitStorageProvider(local, privateSlot);
    }

    [Fact]
    public void Dispose_disposes_both_slots()
    {
        var provider = Create();

        provider.Dispose();

        Assert.Equal(1, publicSlot.DisposeCount);
        Assert.Equal(1, privateSlot.DisposeCount);
    }
}
