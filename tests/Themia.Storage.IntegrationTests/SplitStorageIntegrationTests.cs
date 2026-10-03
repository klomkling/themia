using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Themia.Storage.Local;
using Themia.Storage.S3;
using Themia.Storage.Urls;
using Xunit;

namespace Themia.Storage.IntegrationTests;

/// <summary>
/// The router over real backends (coord #0152): Garage, a real S3-compatible server, as the public slot and the
/// Local provider as the private slot. Proves that a private key never reaches the bucket, that a public write
/// lands in it with the prefix stripped, and that <see cref="IStorageUrlService"/> mints both kinds of link over
/// the same seam, one of which really downloads. Tests share the bucket, so every key carries its own id.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SplitStorageIntegrationTests : IClassFixture<GarageFixture>, IDisposable
{
    private const string PublicBaseUrl = "https://cdn.example.com";
    private const string PresignedBaseUrl = "https://api.example.com/api/v1/storage";

    private readonly GarageFixture garage;
    private readonly string localRoot = Path.Combine(Path.GetTempPath(), "themia-split-it-" + Guid.NewGuid().ToString("N"));
    private readonly string id = Guid.NewGuid().ToString("N");
    private readonly AmazonS3Client bucketClient;
    private readonly SplitStorageProvider router;
    private readonly StorageUrlService urls;

    public SplitStorageIntegrationTests(GarageFixture garage)
    {
        this.garage = garage;
        bucketClient = garage.CreateClient();

        // The slot builds its own client from options, as an app would: no test-only checksum setting.
        var publicSlot = new S3StorageProvider(new S3StorageOptions
        {
            PublicOnly = true,
            PublicBucketName = GarageFixture.Bucket,
            PublicBaseUrl = PublicBaseUrl,
            ServiceUrl = garage.ServiceUrl,
            ForcePathStyle = true,
            AccessKey = GarageFixture.AccessKey,
            SecretKey = GarageFixture.SecretKey,
            Region = GarageFixture.Region,
        });
        var privateSlot = new LocalStorageProvider(new LocalStorageOptions
        {
            RootPath = localRoot,
            SigningKey = "k-long-enough-for-an-hmac-key-0123",
        });

        router = new SplitStorageProvider(publicSlot, privateSlot);
        urls = new StorageUrlService(router, Options.Create(new StorageUrlOptions { PresignedBaseUrl = PresignedBaseUrl }));
    }

    public void Dispose()
    {
        router.Dispose();
        bucketClient.Dispose();
        if (Directory.Exists(localRoot))
        {
            Directory.Delete(localRoot, recursive: true);
        }
    }

    private async Task SeedBucketObjectAsync(string bucketKey, byte[] bytes)
    {
        using var body = new MemoryStream(bytes);
        await bucketClient.PutObjectAsync(new PutObjectRequest { BucketName = GarageFixture.Bucket, Key = bucketKey, InputStream = body });
    }

    private async Task<List<string>> BucketKeysAsync(string prefix) =>
        (await bucketClient.ListObjectsV2Async(new ListObjectsV2Request { BucketName = GarageFixture.Bucket, Prefix = prefix }))
            .S3Objects?.Select(o => o.Key).ToList() ?? [];

    [Fact]
    public async Task A_private_key_is_stored_by_the_Local_slot_and_never_reaches_the_bucket()
    {
        await router.PutAsync($"verifications/{id}/id.pdf", new MemoryStream([9, 9, 9]), new StoragePutOptions("application/pdf"));

        Assert.True(await router.ExistsAsync($"verifications/{id}/id.pdf"));
        Assert.True(Directory.EnumerateFiles(localRoot, "*", SearchOption.AllDirectories).Any());
        Assert.Empty(await BucketKeysAsync($"verifications/{id}"));
    }

    [Fact]
    public async Task A_public_object_written_through_the_router_lands_in_the_bucket_with_the_prefix_stripped()
    {
        var bucketKey = $"split-{id}/listings/1/a.jpg";

        await router.PutAsync($"public/{bucketKey}", new MemoryStream([7, 7, 7]), new StoragePutOptions("image/jpeg", Visibility: StorageVisibility.Public));

        using var stored = await bucketClient.GetObjectAsync(GarageFixture.Bucket, bucketKey);
        using var ms = new MemoryStream();
        await stored.ResponseStream.CopyToAsync(ms);
        Assert.Equal([7, 7, 7], ms.ToArray());
        Assert.Equal([bucketKey], await BucketKeysAsync($"split-{id}"));
        Assert.Empty(await BucketKeysAsync($"public/split-{id}"));
        Assert.False(Directory.Exists(localRoot) && Directory.EnumerateFiles(localRoot, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task A_public_key_is_read_from_the_bucket_and_the_same_name_without_the_prefix_is_not()
    {
        var bucketKey = $"split-{id}/listings/1/a.jpg";
        await SeedBucketObjectAsync(bucketKey, [1, 2, 3]);

        var publicRead = await router.GetAsync($"public/{bucketKey}");
        var privateRead = await router.GetAsync(bucketKey);

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
        var bucketKey = $"split-{id}/listings/2/b.jpg";
        await SeedBucketObjectAsync(bucketKey, [4, 5, 6]);

        var privateUrl = await urls.GetDownloadUrlAsync($"verifications/{id}/id.pdf", TimeSpan.FromMinutes(5));
        var publicUrl = await urls.GetDownloadUrlAsync($"public/{bucketKey}", TimeSpan.FromMinutes(5));

        Assert.StartsWith(PresignedBaseUrl + "/_local/get?", privateUrl.AbsoluteUri);
        var expectedStart = $"{garage.ServiceUrl.AbsoluteUri}{GarageFixture.Bucket}/{bucketKey}?";
        Assert.True(publicUrl.AbsoluteUri.StartsWith(expectedStart, StringComparison.Ordinal), publicUrl.AbsoluteUri);

        // Garage listens on plain http, and the presigned URL follows the endpoint's scheme: it opens as minted.
        using var http = new HttpClient();
        Assert.Equal([4, 5, 6], await http.GetByteArrayAsync(publicUrl));
    }

    [Fact]
    public void GetPublicUrl_composes_the_configured_base_with_the_stripped_key()
    {
        Assert.Equal($"{PublicBaseUrl}/listings/1/a.jpg", router.GetPublicUrl("public/listings/1/a.jpg").ToString());
    }
}
