using Themia.Storage.Local;
using Xunit;

namespace Themia.Storage.Tests;

/// <summary>
/// The public-container invariants hold for a provider built the way an adopter builds one: with the constructor
/// and no <c>LocalStorageOptions.Validate()</c> call (coord #0154). Equal roots would let a route that serves the
/// public container serve private blobs anonymously, and a relative base url yields <c>file:///</c> links on Unix.
/// </summary>
public sealed class LocalPublicContainerConstructionTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "themia-local-ctor", Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_public_root_equal_to_the_private_root_is_refused_without_calling_Validate()
    {
        var error = Assert.Throws<ArgumentException>(() => new LocalStorageProvider(new LocalStorageOptions
        {
            RootPath = Root,
            PublicRootPath = Root,
            PublicBaseUrl = "https://cdn.example.com/media",
        }));

        Assert.Contains("PublicRootPath must differ from RootPath", error.Message);
    }

    [Theory]
    [InlineData("blobs", "pub")]
    [InlineData("content-types", "pub")]
    [InlineData("blobs", "")]
    public void A_public_root_inside_the_private_stores_subtree_is_refused(string store, string tail)
    {
        // {Root}/blobs/pub/blobs/x is also private key "pub/blobs/x" and public key "public/x": one file, two keys.
        var nested = Path.Combine(Root, store, tail);

        var error = Assert.Throws<ArgumentException>(() => new LocalStorageProvider(new LocalStorageOptions
        {
            RootPath = Root,
            PublicRootPath = nested,
            PublicBaseUrl = "https://cdn.example.com/media",
        }));

        Assert.Contains("PublicRootPath", error.Message);
    }

    [Theory]
    [InlineData("blobs", "private")]
    [InlineData("content-types", "private")]
    public void A_private_root_inside_the_public_stores_subtree_is_refused(string store, string tail)
    {
        // Every private blob would sit inside the public tree and be served as public/<tail>/blobs/<key>.
        var publicRoot = Root + "-public";

        var error = Assert.Throws<ArgumentException>(() => new LocalStorageProvider(new LocalStorageOptions
        {
            RootPath = Path.Combine(publicRoot, store, tail),
            PublicRootPath = publicRoot,
            PublicBaseUrl = "https://cdn.example.com/media",
        }));

        Assert.Contains("RootPath", error.Message);
    }

    [Theory]
    [InlineData("public")]
    [InlineData("blobs-public")]
    [InlineData("other/public")]
    public void A_public_root_beside_the_private_stores_is_accepted(string tail)
    {
        var provider = new LocalStorageProvider(new LocalStorageOptions
        {
            RootPath = Root,
            PublicRootPath = Path.Combine(Root, tail),
            PublicBaseUrl = "https://cdn.example.com/media",
        });

        Assert.NotNull(provider);
    }

    [Theory]
    [InlineData("/media")]
    [InlineData("media")]
    [InlineData("ftp://cdn.example.com/media")]
    public void A_public_base_url_that_is_not_absolute_http_is_refused_without_calling_Validate(string baseUrl)
    {
        var error = Assert.Throws<ArgumentException>(() => new LocalStorageProvider(new LocalStorageOptions
        {
            RootPath = Root,
            PublicRootPath = Root + "-public",
            PublicBaseUrl = baseUrl,
        }));

        Assert.Contains("PublicBaseUrl", error.Message);
    }

    [Fact]
    public void A_provider_with_no_public_container_still_constructs()
    {
        var provider = new LocalStorageProvider(new LocalStorageOptions { RootPath = Root });

        Assert.Null(provider.PublicBaseUrl);
    }
}
