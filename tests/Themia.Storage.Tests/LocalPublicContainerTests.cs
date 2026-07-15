using System.Text;
using Themia.Storage;
using Themia.Storage.Local;
using Xunit;

namespace Themia.Storage.Tests;

public sealed class LocalPublicContainerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "themia-local-private", Guid.NewGuid().ToString("N"));
    private readonly string publicRoot = Path.Combine(Path.GetTempPath(), "themia-local-public", Guid.NewGuid().ToString("N"));

    private LocalStorageProvider Create() => new(new LocalStorageOptions
    {
        RootPath = root,
        PublicRootPath = publicRoot,
        PublicBaseUrl = "https://cdn.example.com/media",
        SigningKey = "test-signing-key-at-least-32-characters-long",
    });

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        if (Directory.Exists(publicRoot)) Directory.Delete(publicRoot, recursive: true);
    }

    [Fact]
    public async Task Public_and_private_keys_with_the_same_tail_are_different_objects()
    {
        var provider = Create();
        await provider.PutAsync("t1/a.txt", new MemoryStream(Encoding.UTF8.GetBytes("private")), new StoragePutOptions("text/plain"));
        await provider.PutAsync("public/t1/a.txt", new MemoryStream(Encoding.UTF8.GetBytes("public")), new StoragePutOptions("text/plain"));

        var priv = await provider.GetAsync("t1/a.txt");
        var pub = await provider.GetAsync("public/t1/a.txt");

        Assert.Equal("private", await new StreamReader(priv!.Content).ReadToEndAsync());
        Assert.Equal("public", await new StreamReader(pub!.Content).ReadToEndAsync());
    }

    [Fact]
    public async Task A_public_object_is_written_under_the_public_root_only()
    {
        var provider = Create();
        await provider.PutAsync("public/t1/a.txt", new MemoryStream([1, 2, 3]), new StoragePutOptions("text/plain"));

        Assert.True(File.Exists(Path.Combine(publicRoot, "blobs", "t1", "a.txt")), "public blob must live under PublicRootPath with the prefix stripped");
        Assert.False(Directory.Exists(Path.Combine(root, "blobs", "t1")), "nothing may be written under the private root");
    }

    [Fact]
    public void GetPublicUrl_composes_the_configured_base_with_the_stripped_key()
    {
        Assert.Equal("https://cdn.example.com/media/t1/a.txt", Create().GetPublicUrl("public/t1/a.txt").ToString());
    }

    [Fact]
    public void GetPublicUrl_throws_for_a_private_key()
    {
        // A URL that looks right and 403s at render time is the worst failure mode; fail at the call site.
        Assert.Throws<InvalidOperationException>(() => Create().GetPublicUrl("t1/a.txt"));
    }

    [Fact]
    public void GetPublicUrl_throws_when_no_public_container_is_configured()
    {
        var provider = new LocalStorageProvider(new LocalStorageOptions { RootPath = root, SigningKey = "k" });
        Assert.Throws<InvalidOperationException>(() => provider.GetPublicUrl("public/t1/a.txt"));
    }

    [Fact]
    public void Validate_rejects_a_relative_PublicBaseUrl()
    {
        // The exact ezy-assets bug: a base path that cannot produce an absolute URL. Fail at STARTUP.
        var options = new LocalStorageOptions
        {
            RootPath = root,
            SigningKey = "test-signing-key-at-least-32-characters-long",
            PublicRootPath = publicRoot,
            PublicBaseUrl = "/uploads",
        };
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void Validate_rejects_a_public_root_without_a_base_url()
    {
        var options = new LocalStorageOptions
        {
            RootPath = root,
            SigningKey = "test-signing-key-at-least-32-characters-long",
            PublicRootPath = publicRoot,
        };
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void Validate_rejects_a_base_url_without_a_public_root()
    {
        // Mirror of the above: the public container is both-or-neither, so a base URL with no root is broken.
        var options = new LocalStorageOptions
        {
            RootPath = root,
            SigningKey = "test-signing-key-at-least-32-characters-long",
            PublicBaseUrl = "https://cdn.example.com/media",
        };
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void Validate_rejects_a_public_root_differing_only_in_case_on_case_insensitive_filesystems()
    {
        // On Windows (NTFS) and macOS (APFS) "/data/Store" and "/data/store" are the SAME directory, so a
        // case-only difference must be rejected there or public and private objects would share a container.
        // On a case-sensitive FS they are legitimately different directories, so only assert on Win/macOS.
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var options = new LocalStorageOptions
        {
            RootPath = root,
            SigningKey = "test-signing-key-at-least-32-characters-long",
            PublicRootPath = root.ToUpperInvariant(),
            PublicBaseUrl = "https://cdn.example.com/media",
        };
        Assert.Throws<ArgumentException>(options.Validate);
    }
}
