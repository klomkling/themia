# Themia.Storage Public URLs — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give `Themia.Storage` permanent, unsigned, absolute public URLs for world-readable media, by routing public objects to a second container per provider.

**Architecture:** Visibility is a property of the **container**, not the object (R2 has no per-object ACL; S3 `bucket owner enforced` disables object ACLs). Each provider gains a second container (a public root dir for Local, a public bucket for S3/R2) plus an absolute `PublicBaseUrl`. The **physical key carries its own container address** — `public/{tenant}/{key}` for public, unprefixed `{tenant}/{key}` for private — so `GetAsync`/`StatAsync`/`ExistsAsync`/`DeleteAsync`/`GetPresignedUrlAsync` keep working untouched. **Visibility is set at write and is immutable**: there is no move operation, therefore no operation spans two containers, therefore the design has **no partial-failure state at all**.

**Tech Stack:** .NET 10 + .NET 8 (neutral cores multi-target), xUnit, FluentMigrator (owns all DDL — never `dotnet ef migrations add`), AWSSDK.S3, EF Core + Dapper (both must see the new column).

**Spec:** `docs/superpowers/specs/2026-07-14-storage-public-url-design.md` (rev 2). **Request:** coord #0022.

## Global Constraints

- Target version **0.9.0**, flagged **(breaking)** for direct implementors of `IStorageProvider` (it gains a member). Set in `Directory.Build.props`; note in `CHANGELOG.md` + `MIGRATION.md`.
- `TreatWarningsAsErrors=true` — a warning fails the build. `GenerateDocumentationFile=true` — **every public member needs an XML doc comment** or it fails `RS0016`.
- Cross-cutting packages track PublicAPI: **every new public member must be added to `PublicAPI.Unshipped.txt`** in its project, or a clean build fails with `RS0016`.
- `System.Text.Json` only — never `Newtonsoft.Json`. Log via `ILogger<T>` only.
- Schema/DDL is **FluentMigrator-owned**. The storage schema supports **PostgreSQL and SQL Server only** (see `StorageSchemaMigration`), and a new migration must keep that `IfDatabase` shape.
- Private object keys must stay **byte-identical** to today. No existing blob may move. This is the back-compat contract; Task 5 pins it with a test.
- Build/test from `Packages/themia/`: `dotnet build Themia.sln --no-incremental`, `dotnet test Themia.sln`.

---

### Task 1: `StorageVisibility` + the reserved public key prefix (neutral core)

**Files:**
- Create: `src/neutral/Themia.Storage/StorageVisibility.cs`
- Modify: `src/neutral/Themia.Storage/StorageKey.cs`
- Modify: `src/neutral/Themia.Storage/StorageContracts.cs` (add `Visibility` to `StoragePutOptions`)
- Modify: `src/neutral/Themia.Storage/PublicAPI.Unshipped.txt`
- Test: `tests/Themia.Storage.Tests/StorageKeyTests.cs` (**create** — that project currently holds only `LocalStorageProviderTests.cs` and `LocalUrlSignerTests.cs`)

**Interfaces:**
- Produces: `Themia.Storage.StorageVisibility { Private, Public }`; `StorageKey.PublicPrefix` (`"public/"`), `StorageKey.IsPublic(string key) -> bool`, `StorageKey.StripVisibilityPrefix(string key) -> string`; `StoragePutOptions.Visibility` (defaults to `StorageVisibility.Private`).

- [ ] **Step 1: Write the failing test**

Create `tests/Themia.Storage.Tests/StorageKeyTests.cs`:

```csharp
using Themia.Storage;
using Xunit;

namespace Themia.Storage.Tests;

public sealed class StorageKeyTests
{
    [Theory]
[InlineData("public/t1/a.jpg", true)]
[InlineData("t1/a.jpg", false)]
[InlineData("publicity/t1/a.jpg", false)] // prefix match must be on the whole first SEGMENT
public void IsPublic_matches_only_the_whole_first_segment(string key, bool expected)
{
    Assert.Equal(expected, StorageKey.IsPublic(key));
}

[Fact]
public void StripVisibilityPrefix_removes_the_public_segment_and_leaves_private_keys_alone()
{
    Assert.Equal("t1/a.jpg", StorageKey.StripVisibilityPrefix("public/t1/a.jpg"));
    Assert.Equal("t1/a.jpg", StorageKey.StripVisibilityPrefix("t1/a.jpg"));
}

[Fact]
public void StoragePutOptions_defaults_to_private()
{
    Assert.Equal(StorageVisibility.Private, new StoragePutOptions("image/png").Visibility);
}
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.Storage.Tests/Themia.Storage.Tests.csproj --filter "FullyQualifiedName~StorageKeyTests"`
Expected: FAIL — `StorageKey` does not contain a definition for `IsPublic`.

- [ ] **Step 3: Write minimal implementation**

Create `src/neutral/Themia.Storage/StorageVisibility.cs`:

```csharp
namespace Themia.Storage;

/// <summary>Whether an object is world-readable. Chosen when the object is written and <b>immutable</b>:
/// visibility selects the storage container, and no operation moves an object between containers.</summary>
public enum StorageVisibility
{
    /// <summary>Reachable only through the authenticated module endpoints or a presigned URL.</summary>
    Private,

    /// <summary>World-readable at a permanent, unsigned URL (<see cref="IStorageProvider.GetPublicUrl"/>).</summary>
    Public,
}
```

Add to `src/neutral/Themia.Storage/StorageKey.cs`, inside `public static class StorageKey`:

```csharp
    /// <summary>The reserved first segment marking a physical key as living in the public container.
    /// A routing marker only: it is stripped before the key reaches the container, so it never appears in
    /// a stored key or a public URL. Private keys are deliberately left unprefixed, so every object
    /// written before this feature keeps its exact key and no blob has to move.</summary>
    public const string PublicPrefix = "public/";

    /// <summary>Whether <paramref name="key"/> addresses the public container.</summary>
    /// <param name="key">The physical object key.</param>
    /// <returns><see langword="true"/> when the key's first segment is the reserved public prefix.</returns>
    public static bool IsPublic(string key) =>
        key is not null && key.StartsWith(PublicPrefix, StringComparison.Ordinal);

    /// <summary>Removes the visibility prefix, yielding the key as the container stores it.</summary>
    /// <param name="key">The physical object key.</param>
    /// <returns>The key without its <see cref="PublicPrefix"/>; an unprefixed key is returned unchanged.</returns>
    public static string StripVisibilityPrefix(string key) =>
        IsPublic(key) ? key[PublicPrefix.Length..] : key;
```

In `src/neutral/Themia.Storage/StorageContracts.cs`, replace the `StoragePutOptions` record:

```csharp
/// <summary>Options for writing an object.</summary>
/// <param name="ContentType">The MIME content type to store and serve.</param>
/// <param name="Metadata">Optional provider metadata (small string pairs); may be empty.</param>
/// <param name="Overwrite">Whether to overwrite an existing object at the key (default true).</param>
/// <param name="Visibility">Which container the object is written to. Defaults to
/// <see cref="StorageVisibility.Private"/> and is <b>immutable</b> once written.</param>
public readonly record struct StoragePutOptions(
    string ContentType,
    IReadOnlyDictionary<string, string>? Metadata = null,
    bool Overwrite = true,
    StorageVisibility Visibility = StorageVisibility.Private);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Themia.Storage.Tests/Themia.Storage.Tests.csproj --filter "FullyQualifiedName~StorageKeyTests"`
Expected: PASS.

- [ ] **Step 5: Update PublicAPI and build clean**

Add to `src/neutral/Themia.Storage/PublicAPI.Unshipped.txt`:

```
Themia.Storage.StorageVisibility
Themia.Storage.StorageVisibility.Private = 0 -> Themia.Storage.StorageVisibility
Themia.Storage.StorageVisibility.Public = 1 -> Themia.Storage.StorageVisibility
Themia.Storage.StoragePutOptions.Visibility.get -> Themia.Storage.StorageVisibility
Themia.Storage.StoragePutOptions.Visibility.init -> void
const Themia.Storage.StorageKey.PublicPrefix = "public/" -> string!
static Themia.Storage.StorageKey.IsPublic(string! key) -> bool
static Themia.Storage.StorageKey.StripVisibilityPrefix(string! key) -> string!
```

Run: `dotnet build Themia.sln --no-incremental`
Expected: `Build succeeded.` with no RS0016 warnings. (If RS0016 appears, the PublicAPI line above does not match the emitted signature — copy the exact signature from the diagnostic message.)

- [ ] **Step 6: Commit**

```bash
git add src/neutral/Themia.Storage tests/Themia.Storage.Tests
git commit -m "feat: add StorageVisibility and the reserved public key prefix"
```

---

### Task 2: `LocalStorageProvider` — two containers + `GetPublicUrl`

**Files:**
- Modify: `src/neutral/Themia.Storage/Local/LocalStorageOptions.cs`
- Modify: `src/neutral/Themia.Storage/Local/LocalStorageProvider.cs`
- Modify: `src/neutral/Themia.Storage/PublicAPI.Unshipped.txt`
- Test: `tests/Themia.Storage.Tests/LocalPublicContainerTests.cs` (create)

**Interfaces:**
- Consumes: `StorageKey.IsPublic`, `StorageKey.StripVisibilityPrefix`, `StorageVisibility` (Task 1).
- Produces: `LocalStorageOptions.PublicRootPath`, `LocalStorageOptions.PublicBaseUrl`; `LocalStorageProvider.GetPublicUrl(string key) -> Uri`.

The provider currently resolves every path through `ResolveUnder(subdir, key)` against `options.RootPath`. Change it to pick the root from the key's prefix, then strip the prefix. **A public key and a private key with the same tail therefore land in different roots and never collide.**

- [ ] **Step 1: Write the failing test**

Create `tests/Themia.Storage.Tests/LocalPublicContainerTests.cs`:

```csharp
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
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.Storage.Tests/Themia.Storage.Tests.csproj --filter "FullyQualifiedName~LocalPublicContainerTests"`
Expected: FAIL — `LocalStorageOptions` does not contain a definition for `PublicRootPath`.

- [ ] **Step 3: Add the options**

In `src/neutral/Themia.Storage/Local/LocalStorageOptions.cs`, add the two properties and extend `Validate`:

```csharp
    /// <summary>The root directory for <see cref="StorageVisibility.Public"/> objects. Leave unset to
    /// disable the public container (any attempt to write or address a public object then throws).
    /// Must be a different directory from <see cref="RootPath"/>.</summary>
    public string PublicRootPath { get; set; } = string.Empty;

    /// <summary>The <b>absolute</b> base URL public objects are served from — for Local this is the app's
    /// origin plus the storage endpoint mount (e.g. <c>https://api.example.com/storage/public</c>).
    /// Required when <see cref="PublicRootPath"/> is set. Resolved at READ time, never persisted: a URL
    /// frozen at upload time cannot survive a CDN swap or a domain change.</summary>
    public string PublicBaseUrl { get; set; } = string.Empty;
```

Replace `Validate`:

```csharp
    /// <summary>Validates that required options are set, failing fast at composition time.</summary>
    /// <exception cref="ArgumentException">Thrown when <see cref="RootPath"/> or <see cref="SigningKey"/> is
    /// null or whitespace, when a public container is configured without an absolute
    /// <see cref="PublicBaseUrl"/>, or when the public root equals the private root.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RootPath)) throw new ArgumentException("RootPath must be set.", nameof(RootPath));
        if (string.IsNullOrWhiteSpace(SigningKey)) throw new ArgumentException("SigningKey must be set (required to issue/verify Local presigned download/upload URLs).", nameof(SigningKey));

        if (string.IsNullOrWhiteSpace(PublicRootPath))
        {
            return;
        }

        // A relative base URL is the ezy-assets bug in a bottle: it cannot be hot-linked cross-origin, and
        // a photo whose URL was resolved at upload time freezes that relative path in the database forever.
        if (!Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out _))
        {
            throw new ArgumentException(
                "PublicBaseUrl must be set to an ABSOLUTE url (e.g. https://api.example.com/storage/public) when PublicRootPath is set.",
                nameof(PublicBaseUrl));
        }

        if (string.Equals(Path.GetFullPath(RootPath), Path.GetFullPath(PublicRootPath), StringComparison.Ordinal))
        {
            throw new ArgumentException("PublicRootPath must differ from RootPath; public and private objects cannot share a container.", nameof(PublicRootPath));
        }
    }
```

- [ ] **Step 4: Route by container in the provider**

In `src/neutral/Themia.Storage/Local/LocalStorageProvider.cs`, replace `ResolveUnder` and add `GetPublicUrl`:

```csharp
    /// <inheritdoc />
    public Uri GetPublicUrl(string key)
    {
        if (!StorageKey.IsPublic(key))
        {
            throw new InvalidOperationException(
                $"Object '{key}' is not in the public container; only a public object has a public URL. " +
                "Public visibility is chosen at write time and cannot be changed.");
        }

        if (string.IsNullOrWhiteSpace(options.PublicBaseUrl))
        {
            throw new InvalidOperationException("No public container is configured; set LocalStorageOptions.PublicRootPath and PublicBaseUrl.");
        }

        return new Uri($"{options.PublicBaseUrl.TrimEnd('/')}/{StorageKey.StripVisibilityPrefix(key)}");
    }

    // Maps a key to an absolute path UNDER {root}/{subdir}, rejecting traversal/absolute keys by verifying
    // the resolved full path stays within that subtree. The key's own prefix selects the root: a public key
    // resolves under PublicRootPath with the prefix stripped, so a public and a private key with the same
    // tail are different objects in different containers and can never collide.
    private string ResolveUnder(string subdir, string key)
    {
        var isPublic = StorageKey.IsPublic(key);
        if (isPublic && string.IsNullOrWhiteSpace(options.PublicRootPath))
        {
            throw new InvalidOperationException("No public container is configured; set LocalStorageOptions.PublicRootPath and PublicBaseUrl.");
        }

        var root = isPublic ? options.PublicRootPath : options.RootPath;
        var normalized = StorageKey.NormalizeAndValidate(StorageKey.StripVisibilityPrefix(key));

        var subRoot = Path.GetFullPath(Path.Combine(root, subdir));
        var full = Path.GetFullPath(Path.Combine(subRoot, normalized));
        if (!full.StartsWith(subRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !full.Equals(subRoot, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Invalid object key '{key}': resolves outside the storage root.", nameof(key));
        }

        return full;
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Themia.Storage.Tests/Themia.Storage.Tests.csproj`
Expected: PASS — the new class plus every pre-existing Local test (private keys must be unaffected).

- [ ] **Step 6: Update PublicAPI, build clean, commit**

Add to `src/neutral/Themia.Storage/PublicAPI.Unshipped.txt`:

```
Themia.Storage.Local.LocalStorageOptions.PublicRootPath.get -> string!
Themia.Storage.Local.LocalStorageOptions.PublicRootPath.set -> void
Themia.Storage.Local.LocalStorageOptions.PublicBaseUrl.get -> string!
Themia.Storage.Local.LocalStorageOptions.PublicBaseUrl.set -> void
Themia.Storage.Local.LocalStorageProvider.GetPublicUrl(string! key) -> System.Uri!
```

```bash
dotnet build Themia.sln --no-incremental
git add src/neutral/Themia.Storage tests/Themia.Storage.Tests
git commit -m "feat: Local provider routes public objects to a second root and composes public URLs"
```

---

### Task 3: `S3StorageProvider` — two buckets + `GetPublicUrl`

**Files:**
- Modify: `src/neutral/Themia.Storage.S3/S3StorageOptions.cs`
- Modify: `src/neutral/Themia.Storage.S3/S3StorageProvider.cs`
- Modify: `src/neutral/Themia.Storage.S3/PublicAPI.Unshipped.txt`
- Modify: `tests/Themia.Storage.Tests/Themia.Storage.Tests.csproj` — add `<ProjectReference Include="../../src/neutral/Themia.Storage.S3/Themia.Storage.S3.csproj" />`
- Test: `tests/Themia.Storage.Tests/S3PublicContainerTests.cs` (create)

**Do not put these in `tests/Themia.Storage.IntegrationTests`.** That project is the only one referencing `Themia.Storage.S3` today, but it spins up a **MinIO container via Testcontainers** — these are pure URL/routing assertions that need no network, so they belong in the fast unit project, which just needs the S3 project reference added.

**Interfaces:**
- Consumes: `StorageKey.IsPublic`, `StorageKey.StripVisibilityPrefix` (Task 1).
- Produces: `S3StorageOptions.PublicBucketName`, `S3StorageOptions.PublicBaseUrl`; `S3StorageProvider.GetPublicUrl(string key) -> Uri`.

`S3StorageProvider` holds a single `private readonly string bucket` used by every method. Replace each `BucketName = bucket` / `Key = key` pair with a resolved `(bucket, key)` from the key's prefix.

- [ ] **Step 1: Write the failing test**

Create `tests/Themia.Storage.Tests/S3PublicContainerTests.cs`. These are pure URL/routing tests — no network, no MinIO — so they never construct a client; `GetPublicUrl` needs none.

```csharp
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
        var options = new S3StorageOptions
        {
            BucketName = "private-bucket",
            PublicBucketName = "public-bucket",
            PublicBaseUrl = "/media",
        };
        Assert.Throws<ArgumentException>(options.Validate);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.Storage.Tests/Themia.Storage.Tests.csproj --filter "FullyQualifiedName~S3PublicContainerTests"`
Expected: FAIL — `S3StorageOptions` does not contain a definition for `PublicBucketName`.

- [ ] **Step 3: Add the options + a `Validate`**

Add to `src/neutral/Themia.Storage.S3/S3StorageOptions.cs`, inside the class:

```csharp
    /// <summary>The bucket for <see cref="Themia.Storage.StorageVisibility.Public"/> objects. It must be a
    /// <b>public-read</b> bucket (or one fronted by a public custom domain / CDN). Leave unset to disable
    /// the public container. Separate buckets are not a style choice: R2 has no per-object ACL, and S3
    /// Object Ownership defaults to <c>bucket owner enforced</c>, which disables object ACLs entirely — so
    /// "public" can only mean "in the public bucket".</summary>
    public string PublicBucketName { get; set; } = string.Empty;

    /// <summary>The <b>absolute</b> base URL public objects are served from — the public bucket's custom
    /// domain or the CDN in front of it (e.g. <c>https://cdn.example.com</c>). Required when
    /// <see cref="PublicBucketName"/> is set. Bytes are served straight from the bucket; the app is never
    /// in the request path.</summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>Validates the public-container options, failing fast at composition time.</summary>
    /// <exception cref="ArgumentException">A public bucket is configured without an absolute
    /// <see cref="PublicBaseUrl"/>, or the public bucket equals the private one.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(PublicBucketName))
        {
            return;
        }

        if (!Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out _))
        {
            throw new ArgumentException(
                "PublicBaseUrl must be set to an ABSOLUTE url (e.g. https://cdn.example.com) when PublicBucketName is set.",
                nameof(PublicBaseUrl));
        }

        if (string.Equals(BucketName, PublicBucketName, StringComparison.Ordinal))
        {
            throw new ArgumentException("PublicBucketName must differ from BucketName; public and private objects cannot share a container.", nameof(PublicBucketName));
        }
    }
```

- [ ] **Step 4: Route by container in the provider**

In `src/neutral/Themia.Storage.S3/S3StorageProvider.cs`:

Add the fields and set them in **both** constructors (the `(S3StorageOptions)` one reads them from options; the `(IAmazonS3, string)` test constructor leaves them empty — add an optional third parameter so MinIO tests can exercise the public bucket later):

```csharp
    private readonly string publicBucket;
    private readonly string publicBaseUrl;
```

In the `S3StorageOptions` constructor, after `bucket = options.BucketName;`:

```csharp
        options.Validate();
        publicBucket = options.PublicBucketName;
        publicBaseUrl = options.PublicBaseUrl;
```

In the `(IAmazonS3 client, string bucketName)` constructor, add `publicBucket = string.Empty; publicBaseUrl = string.Empty;`.

Add the resolver and `GetPublicUrl`:

```csharp
    /// <inheritdoc />
    public Uri GetPublicUrl(string key)
    {
        if (!Themia.Storage.StorageKey.IsPublic(key))
        {
            throw new InvalidOperationException(
                $"Object '{key}' is not in the public container; only a public object has a public URL. " +
                "Public visibility is chosen at write time and cannot be changed.");
        }

        if (string.IsNullOrWhiteSpace(publicBaseUrl))
        {
            throw new InvalidOperationException("No public container is configured; set S3StorageOptions.PublicBucketName and PublicBaseUrl.");
        }

        return new Uri($"{publicBaseUrl.TrimEnd('/')}/{Themia.Storage.StorageKey.StripVisibilityPrefix(key)}");
    }

    // The key addresses its own container: a "public/" prefix selects the public bucket and is stripped,
    // so the object is stored under the same tail in a different bucket. Every S3 call routes through here.
    private (string Bucket, string Key) Resolve(string key)
    {
        if (!Themia.Storage.StorageKey.IsPublic(key))
        {
            return (bucket, key);
        }

        if (string.IsNullOrWhiteSpace(publicBucket))
        {
            throw new InvalidOperationException("No public container is configured; set S3StorageOptions.PublicBucketName and PublicBaseUrl.");
        }

        return (publicBucket, Themia.Storage.StorageKey.StripVisibilityPrefix(key));
    }
```

Then, in **every** method that touches S3 (`PutAsync`, `GetAsync`, `ExistsAsync`, `StatAsync`, `DeleteAsync`, `GetPresignedUrlAsync`), replace the direct use of `bucket`/`key` with the resolved pair. For example, `GetPresignedUrlAsync` becomes:

```csharp
    public async Task<Uri> GetPresignedUrlAsync(string key, PresignedUrlRequest request, CancellationToken cancellationToken = default)
    {
        var (resolvedBucket, resolvedKey) = Resolve(key);
        var url = await client.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = resolvedBucket,
            Key = resolvedKey,
            Verb = request.Operation == PresignedUrlOperation.Put ? HttpVerb.PUT : HttpVerb.GET,
            // ...the remaining property assignments are unchanged
```

Apply the identical `var (resolvedBucket, resolvedKey) = Resolve(key);` substitution in the other five methods. **Do not leave any raw `bucket` reference in a request object** — grep for it: `grep -n "BucketName = bucket" src/neutral/Themia.Storage.S3/S3StorageProvider.cs` must return nothing.

- [ ] **Step 5: Run tests + grep to verify**

Run: `dotnet test tests/Themia.Storage.Tests/Themia.Storage.Tests.csproj --filter "FullyQualifiedName~S3PublicContainerTests"`
Expected: PASS.

Run: `grep -n "BucketName = bucket" src/neutral/Themia.Storage.S3/S3StorageProvider.cs`
Expected: no output (every call site routes through `Resolve`).

- [ ] **Step 6: Update PublicAPI, build clean, commit**

Add to `src/neutral/Themia.Storage.S3/PublicAPI.Unshipped.txt`:

```
Themia.Storage.S3.S3StorageOptions.PublicBucketName.get -> string!
Themia.Storage.S3.S3StorageOptions.PublicBucketName.set -> void
Themia.Storage.S3.S3StorageOptions.PublicBaseUrl.get -> string!
Themia.Storage.S3.S3StorageOptions.PublicBaseUrl.set -> void
Themia.Storage.S3.S3StorageOptions.Validate() -> void
Themia.Storage.S3.S3StorageProvider.GetPublicUrl(string! key) -> System.Uri!
```

```bash
dotnet build Themia.sln --no-incremental
git add src/neutral/Themia.Storage.S3 tests/Themia.Storage.Tests
git commit -m "feat: S3/R2 provider routes public objects to a second bucket and composes public URLs"
```

---

### Task 4: Put `GetPublicUrl` on `IStorageProvider`

**Files:**
- Modify: `src/neutral/Themia.Storage/IStorageProvider.cs`
- Modify: `src/neutral/Themia.Storage/PublicAPI.Unshipped.txt`
- Modify: `tests/Themia.Modules.Storage.IntegrationTests/StorageConformanceTests.cs` — **`ThrowingStorageProvider` implements `IStorageProvider` and will stop compiling.** It is the only implementor outside the two real providers (verified: `grep -rln ": IStorageProvider" src tests`). This is what "breaking for direct implementors" means in practice, and it is worth seeing it bite here.

Both real providers already have the method (Tasks 2 and 3), so the production code compiles immediately. **This is the breaking change** — hence 0.9.0.

**Interfaces:**
- Consumes: `LocalStorageProvider.GetPublicUrl`, `S3StorageProvider.GetPublicUrl`.
- Produces: `IStorageProvider.GetPublicUrl(string key) -> Uri`.

- [ ] **Step 1: Add the member**

Add to `src/neutral/Themia.Storage/IStorageProvider.cs`, inside the interface:

```csharp
    /// <summary>The permanent, unsigned, absolute URL of a <see cref="StorageVisibility.Public"/> object.
    /// Pure composition of configuration and the key — it performs no I/O and does not check existence.
    /// Synchronous by design, and deliberately <b>not</b> derived from the incoming request: a permanent URL
    /// must survive a background job (which has no <c>HttpContext</c>) and a proxy/CDN (whose internal
    /// origin is not the public one).</summary>
    /// <param name="key">The physical object key (must address the public container).</param>
    /// <returns>The absolute public URL.</returns>
    /// <exception cref="InvalidOperationException">The key is not in the public container, or no public
    /// container is configured. It never returns a URL that would 403 at render time.</exception>
    Uri GetPublicUrl(string key);
```

- [ ] **Step 2: Build and watch the test fake break**

Run: `dotnet build Themia.sln --no-incremental`
Expected: **FAIL** with `CS0535: 'ThrowingStorageProvider' does not implement interface member 'IStorageProvider.GetPublicUrl(string)'`. That is the expected, correct failure — the interface grew a member.

- [ ] **Step 3: Fix the test fake by delegating**

In `tests/Themia.Modules.Storage.IntegrationTests/StorageConformanceTests.cs`, add to `ThrowingStorageProvider` (it wraps `inner`, so delegate like its other members):

```csharp
    public Uri GetPublicUrl(string key) => inner.GetPublicUrl(key);
```

Run: `dotnet build Themia.sln --no-incremental`
Expected: `Build succeeded.`

- [ ] **Step 4: Update PublicAPI and commit**

Add to `src/neutral/Themia.Storage/PublicAPI.Unshipped.txt`:

```
Themia.Storage.IStorageProvider.GetPublicUrl(string! key) -> System.Uri!
```

```bash
dotnet build Themia.sln --no-incremental
git add src/neutral/Themia.Storage tests/Themia.Modules.Storage.IntegrationTests
git commit -m "feat: add GetPublicUrl to IStorageProvider (breaking for external implementors)"
```

---

### Task 5: `StorageScope` — visibility-aware physical keys + reserve the `public` tenant id

**Files:**
- Modify: `src/modules/Themia.Modules.Storage/StorageScope.cs`
- Modify: `src/modules/Themia.Modules.Storage/PublicAPI.Unshipped.txt`
- Test: `tests/Themia.Modules.Storage.Tests/StorageScopeTests.cs` (add to the existing file; create it if absent)

**Interfaces:**
- Consumes: `StorageKey.PublicPrefix`, `StorageVisibility` (Task 1).
- Produces: `StorageScope.PhysicalKey(TenantId? tenantId, string logicalKey, StorageVisibility visibility) -> string`. The existing two-argument overload is kept and delegates with `StorageVisibility.Private`, so no existing caller changes behavior.

**The collision this closes:** with a public key shaped `public/{tenant}/{key}`, a *private* object belonging to a tenant literally named `public` would be `public/{key}` — which could collide with another tenant's public space. `StorageScope` already rejects the reserved `_platform` tenant id for exactly this reason; `public` joins it.

- [ ] **Step 1: Write the failing test**

Add to `tests/Themia.Modules.Storage.Tests/StorageScopeTests.cs`:

```csharp
[Fact]
public void PhysicalKey_prefixes_public_objects_and_leaves_private_ones_byte_identical()
{
    var tenant = new TenantId("t1");

    // BACK-COMPAT CONTRACT: a private key must be exactly what it was before this feature existed.
    // If this assertion ever changes, every already-stored blob would have to be moved.
    Assert.Equal("t1/a.jpg", StorageScope.PhysicalKey(tenant, "a.jpg", StorageVisibility.Private));
    Assert.Equal("t1/a.jpg", StorageScope.PhysicalKey(tenant, "a.jpg"));

    Assert.Equal("public/t1/a.jpg", StorageScope.PhysicalKey(tenant, "a.jpg", StorageVisibility.Public));
}

[Fact]
public void PhysicalKey_prefixes_public_platform_objects()
{
    Assert.Equal("public/_platform/a.jpg", StorageScope.PhysicalKey(null, "a.jpg", StorageVisibility.Public));
}

[Fact]
public void PhysicalKey_rejects_the_reserved_public_tenant_id()
{
    // A tenant named "public" would put its PRIVATE objects at public/{key} — inside the public namespace.
    var ex = Assert.Throws<ArgumentException>(() => StorageScope.PhysicalKey(new TenantId("public"), "a.jpg", StorageVisibility.Private));
    Assert.Contains("reserved", ex.Message, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.Modules.Storage.Tests/Themia.Modules.Storage.Tests.csproj --filter "FullyQualifiedName~StorageScopeTests"`
Expected: FAIL — no overload of `PhysicalKey` takes 3 arguments.

- [ ] **Step 3: Write the implementation**

Replace the body of `src/modules/Themia.Modules.Storage/StorageScope.cs`:

```csharp
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Storage;

namespace Themia.Modules.Storage;

/// <summary>Maps a caller's logical key to the physical key handed to <see cref="Themia.Storage.IStorageProvider"/>,
/// prefixing it with the tenant id (or the platform prefix), and — for a public object — with the reserved
/// visibility prefix that selects the public container. Centralizing both prefixes here is what makes
/// tenant isolation and container routing hold by construction.</summary>
public static class StorageScope
{
    /// <summary>The prefix for platform (tenant-less) objects.</summary>
    public const string PlatformPrefix = "_platform";

    /// <summary>The reserved tenant id that would collide with the public namespace.</summary>
    private const string PublicReservedTenantId = "public";

    /// <summary>Builds the physical key for a <see cref="StorageVisibility.Private"/> object.</summary>
    /// <param name="tenantId">The owning tenant, or <see langword="null"/> for a platform object.</param>
    /// <param name="logicalKey">The caller's key.</param>
    /// <returns>The physical key <c>{tenant}/{key}</c> (or <c>_platform/{key}</c>).</returns>
    public static string PhysicalKey(TenantId? tenantId, string logicalKey) =>
        PhysicalKey(tenantId, logicalKey, StorageVisibility.Private);

    /// <summary>Builds the physical key for <paramref name="logicalKey"/> under <paramref name="tenantId"/>,
    /// addressing the container selected by <paramref name="visibility"/>.</summary>
    /// <param name="tenantId">The owning tenant, or <see langword="null"/> for a platform object.</param>
    /// <param name="logicalKey">The caller's key (validated — rejected if it has a leading '/' or a '..' segment).</param>
    /// <param name="visibility">Which container the object lives in.</param>
    /// <returns><c>{tenant}/{key}</c> for a private object — <b>byte-identical to the pre-0.9.0 key, so no
    /// stored blob ever moves</b> — and <c>public/{tenant}/{key}</c> for a public one.</returns>
    /// <exception cref="ArgumentException">The key is blank, absolute, or contains a '..' segment, or the
    /// tenant id equals a reserved prefix.</exception>
    public static string PhysicalKey(TenantId? tenantId, string logicalKey, StorageVisibility visibility)
    {
        var normalized = StorageKey.NormalizeAndValidate(logicalKey);

        // A tenant whose id equals a reserved prefix would collide at the blob layer, breaking isolation:
        // '_platform' would collide with platform objects, and 'public' would place its PRIVATE objects at
        // public/{key} — inside the public namespace, world-readable. Reject both.
        if (tenantId is { } t)
        {
            if (string.Equals(t.Value, PlatformPrefix, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Tenant id '{PlatformPrefix}' is reserved for platform objects.", nameof(tenantId));
            }

            if (string.Equals(t.Value, PublicReservedTenantId, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Tenant id '{PublicReservedTenantId}' is reserved: it names the public container.", nameof(tenantId));
            }
        }

        var prefix = tenantId?.Value ?? PlatformPrefix;
        var scoped = $"{prefix}/{normalized}";
        return visibility == StorageVisibility.Public ? StorageKey.PublicPrefix + scoped : scoped;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Themia.Modules.Storage.Tests/Themia.Modules.Storage.Tests.csproj`
Expected: PASS — including every pre-existing test (the 2-arg overload is unchanged).

- [ ] **Step 5: Update PublicAPI and commit**

Add to `src/modules/Themia.Modules.Storage/PublicAPI.Unshipped.txt`:

```
static Themia.Modules.Storage.StorageScope.PhysicalKey(Themia.Framework.Core.Abstractions.Tenancy.TenantId? tenantId, string! logicalKey, Themia.Storage.StorageVisibility visibility) -> string!
```

```bash
dotnet build Themia.sln --no-incremental
git add src/modules/Themia.Modules.Storage tests/Themia.Modules.Storage.Tests
git commit -m "feat: StorageScope builds visibility-addressed physical keys; reserve the 'public' tenant id"
```

---

### Task 6: Persist `Visibility` — entity, EF, Dapper, FluentMigrator

**Files:**
- Modify: `src/modules/Themia.Modules.Storage/Entities/StorageObject.cs`
- Modify: `src/modules/Themia.Modules.Storage/EntityConfiguration/StorageModelConfiguration.cs`
- Create: `src/modules/Themia.Modules.Storage/Migrations/StorageVisibilityMigration.cs`
- Modify: `src/modules/Themia.Modules.Storage/PublicAPI.Unshipped.txt`
- Test: `tests/Themia.Data.Migrations.Tests/` — add a migration test alongside the existing ones

The Dapper mapping needs **no change**: `StorageDapperMappings` uses the snake_case convention, which maps `Visibility` → `visibility` automatically (only `ETag` needed an override).

**Interfaces:**
- Consumes: `StorageVisibility` (Task 1).
- Produces: `StorageObject.Visibility` (a `StorageVisibility`, persisted as `int`), column `storage.storage_objects.visibility`.

- [ ] **Step 1: Add the property**

In `src/modules/Themia.Modules.Storage/Entities/StorageObject.cs`, add:

```csharp
    /// <summary>Which container the blob lives in. Set when the object is written and <b>immutable</b>:
    /// changing it would require physically moving bytes between containers, which no operation does.</summary>
    public StorageVisibility Visibility { get; set; } = StorageVisibility.Private;
```

Add `using Themia.Storage;` to the file's usings.

- [ ] **Step 2: Map it in EF**

In `StorageObjectConfiguration.Configure`, add:

```csharp
            // Stored as int so the enum's numeric contract is explicit and stable across engines.
            b.Property(o => o.Visibility).HasColumnName("visibility").HasConversion<int>();
```

- [ ] **Step 3: Write the migration**

Create `src/modules/Themia.Modules.Storage/Migrations/StorageVisibilityMigration.cs`:

```csharp
using System;
using FluentMigrator;

namespace Themia.Modules.Storage.Migrations;

/// <summary>Adds <c>storage_objects.visibility</c>. Every pre-existing object is private — that is the
/// correct default and the reason private physical keys were left unprefixed: no stored blob has to move.</summary>
[Migration(202607150001, "Themia.Storage: add storage_objects.visibility")]
public sealed class StorageVisibilityMigration : Migration
{
    private const string SchemaName = "storage";

    /// <inheritdoc />
    public override void Up()
    {
        IfDatabase("postgresql", "sqlserver").Delegate(() =>
            Create.Column("visibility").OnTable("storage_objects").InSchema(SchemaName)
                .AsInt32().NotNullable().WithDefaultValue(0)); // 0 = StorageVisibility.Private

        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Storage supports only PostgreSQL and SQL Server. The active database provider " +
                "is not supported; add a migration branch for it."));
    }

    /// <inheritdoc />
    public override void Down()
    {
        Delete.Column("visibility").FromTable("storage_objects").InSchema(SchemaName);
    }
}
```

- [ ] **Step 4: Run the migration tests**

Run: `dotnet test tests/Themia.Data.Migrations.Tests/Themia.Data.Migrations.Tests.csproj`
Expected: PASS — the runner applies both storage migrations in order.

- [ ] **Step 5: Update PublicAPI, build clean, commit**

Add to `src/modules/Themia.Modules.Storage/PublicAPI.Unshipped.txt`:

```
Themia.Modules.Storage.Entities.StorageObject.Visibility.get -> Themia.Storage.StorageVisibility
Themia.Modules.Storage.Entities.StorageObject.Visibility.set -> void
Themia.Modules.Storage.Migrations.StorageVisibilityMigration
Themia.Modules.Storage.Migrations.StorageVisibilityMigration.StorageVisibilityMigration() -> void
Themia.Modules.Storage.Migrations.StorageVisibilityMigration.Down() -> void
Themia.Modules.Storage.Migrations.StorageVisibilityMigration.Up() -> void
```

```bash
dotnet build Themia.sln --no-incremental
git add src/modules/Themia.Modules.Storage tests/Themia.Data.Migrations.Tests
git commit -m "feat: persist StorageObject.Visibility (EF + FluentMigrator; Dapper maps by convention)"
```

---

### Task 7: `TenantStorage` — write visibility, address the right container, enforce immutability, `GetPublicUrlAsync`

**Files:**
- Modify: `src/modules/Themia.Modules.Storage/ITenantStorage.cs`
- Modify: `src/modules/Themia.Modules.Storage/TenantStorage.cs`
- Modify: `src/modules/Themia.Modules.Storage/StorageExceptions.cs`
- Modify: `src/modules/Themia.Modules.Storage/PublicAPI.Unshipped.txt`
- Test: `tests/Themia.Modules.Storage.IntegrationTests/TenantStorageVisibilityTests.cs` (create). **`ITenantStorage` is exercised in `Themia.Modules.Storage.IntegrationTests` (see `StorageConformanceTests.cs`), not in the `.Tests` unit project** — that project holds only validator/builder/scope tests. Copy the service-collection harness from the top of `StorageConformanceTests.cs`, and configure `UseLocal` with `PublicRootPath` + `PublicBaseUrl = "https://cdn.example.com"`.

**Interfaces:**
- Consumes: `StorageScope.PhysicalKey(tenant, key, visibility)` (Task 5); `StorageObject.Visibility` (Task 6); `IStorageProvider.GetPublicUrl` (Task 4).
- Produces: `ITenantStorage.GetPublicUrlAsync(string key, CancellationToken ct = default) -> Task<Uri>`; `ITenantStorage.GetUploadUrlAsync(string key, string contentType, long sizeBytes, TimeSpan expiry, StorageVisibility visibility, CancellationToken ct = default)`; `StorageNotPublicException`.

**The key move:** every read/delete in `TenantStorage` **already fetches the `StorageObject` row** before touching the provider (`TenantStorage.cs:157`, `:178`). So each call to `StorageScope.PhysicalKey(tenantContext.CurrentTenantId, key)` becomes `StorageScope.PhysicalKey(tenantContext.CurrentTenantId, key, row.Visibility)` — **zero new queries**.

- [ ] **Step 1: Write the failing test**

Create `tests/Themia.Modules.Storage.IntegrationTests/TenantStorageVisibilityTests.cs`. Copy the fixture setup from `StorageConformanceTests.cs` in the same project verbatim (it builds a real `ServiceCollection` with `AddThemiaStorage().UseLocal(...)`, a `StubCurrentUserAccessor`, and a tenant context), adding `PublicRootPath` + `PublicBaseUrl = "https://cdn.example.com"` to the `UseLocal` options and using tenant `t1`. Then add:

```csharp
[Fact]
public async Task Put_public_then_GetPublicUrl_returns_the_absolute_url()
{
    await storage.PutAsync("hero.jpg", Bytes("x"), new StoragePutOptions("image/jpeg", Visibility: StorageVisibility.Public));

    var url = await storage.GetPublicUrlAsync("hero.jpg");

    Assert.True(url.IsAbsoluteUri);
    Assert.Equal("https://cdn.example.com/t1/hero.jpg", url.ToString());
}

[Fact]
public async Task GetPublicUrl_throws_for_a_private_object()
{
    await storage.PutAsync("invoice.pdf", Bytes("x"), new StoragePutOptions("application/pdf"));

    // The failure must land at the CALL SITE, not as a 403 in someone's browser.
    await Assert.ThrowsAsync<StorageNotPublicException>(() => storage.GetPublicUrlAsync("invoice.pdf"));
}

[Fact]
public async Task GetPublicUrl_throws_for_a_missing_object()
{
    await Assert.ThrowsAsync<StorageNotPublicException>(() => storage.GetPublicUrlAsync("nope.jpg"));
}

[Fact]
public async Task A_public_object_reads_back_through_the_public_container()
{
    await storage.PutAsync("hero.jpg", Bytes("public-bytes"), new StoragePutOptions("image/jpeg", Visibility: StorageVisibility.Public));

    var read = await storage.GetAsync("hero.jpg");

    Assert.NotNull(read);
    Assert.Equal("public-bytes", await new StreamReader(read!.Content).ReadToEndAsync());
}

[Fact]
public async Task Re_putting_an_existing_key_with_a_different_visibility_throws()
{
    await storage.PutAsync("hero.jpg", Bytes("x"), new StoragePutOptions("image/jpeg", Visibility: StorageVisibility.Public));

    // Visibility is immutable. The alternatives are both SILENT failures: writing at the new visibility
    // orphans the old blob; writing at the old one ignores the caller and leaves the app believing it
    // published a photo that is still private.
    var ex = await Assert.ThrowsAsync<StorageValidationException>(() =>
        storage.PutAsync("hero.jpg", Bytes("y"), new StoragePutOptions("image/jpeg", Visibility: StorageVisibility.Private)));
    Assert.Contains("immutable", ex.Message, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.Modules.Storage.IntegrationTests/Themia.Modules.Storage.IntegrationTests.csproj --filter "FullyQualifiedName~TenantStorageVisibilityTests"`
Expected: FAIL — `ITenantStorage` does not contain `GetPublicUrlAsync`.

- [ ] **Step 3: Add the exception**

Append to `src/modules/Themia.Modules.Storage/StorageExceptions.cs`:

```csharp
/// <summary>Thrown when a public URL is requested for an object that is not in the public container (or
/// does not exist). Deliberately an exception rather than a null/placeholder URL: a URL that looks right
/// and 403s at render time is the worst of the available failure modes.</summary>
public sealed class StorageNotPublicException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">The reason no public URL exists.</param>
    public StorageNotPublicException(string message) : base(message) { }
}
```

- [ ] **Step 4: Extend the interface**

In `src/modules/Themia.Modules.Storage/ITenantStorage.cs`, add:

```csharp
    /// <summary>The permanent, unsigned, absolute URL of a public object.</summary>
    /// <param name="key">The logical key.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The absolute public URL.</returns>
    /// <exception cref="StorageNotPublicException">The object is private or does not exist.</exception>
    Task<Uri> GetPublicUrlAsync(string key, CancellationToken cancellationToken = default);
```

and change `GetUploadUrlAsync` to carry visibility, so a presigned upload lands **directly** in the right container (otherwise every large video would need a move — which does not exist):

```csharp
    /// <param name="visibility">Which container the uploaded object lands in. Immutable once written.</param>
    Task<Uri> GetUploadUrlAsync(string key, string contentType, long sizeBytes, TimeSpan expiry, StorageVisibility visibility = StorageVisibility.Private, CancellationToken cancellationToken = default);
```

Add `using Themia.Storage;` if not present.

- [ ] **Step 5: Implement in `TenantStorage`**

In `TenantStorage.PutAsync`, replace the first line after the null check:

```csharp
        // Visibility is immutable: it selects the container, and no operation moves bytes between
        // containers. Re-putting at a different visibility would either orphan the old blob (write to the
        // new container) or silently ignore the caller (write to the old one). Reject it instead.
        var existing = await objects.FirstOrDefaultAsync(new StorageObjectByKeySpec(key, tenantContext.CurrentTenantId, committedOnly: false), cancellationToken).ConfigureAwait(false);
        if (existing is not null && InScope(existing) && existing.Visibility != putOptions.Visibility)
        {
            throw new StorageValidationException(
                $"Object '{key}' already exists with visibility {existing.Visibility}; visibility is immutable. " +
                "Delete the object and re-upload it to change where it lives.");
        }

        var physicalKey = StorageScope.PhysicalKey(tenantContext.CurrentTenantId, key, putOptions.Visibility);
```

In `ReserveAsync` (where the row is created), set `row.Visibility = putOptions.Visibility;` on the newly created row — pass the visibility into `ReserveAsync` as an extra parameter.

In `GetAsync`, `ExistsAsync`, `DeleteAsync`, and `GetDownloadUrlAsync`, change each `StorageScope.PhysicalKey(tenantContext.CurrentTenantId, key)` call to use the row already in hand:

```csharp
        var physicalKey = StorageScope.PhysicalKey(tenantContext.CurrentTenantId, key, row.Visibility);
```

Add the new method:

```csharp
    /// <inheritdoc />
    public async Task<Uri> GetPublicUrlAsync(string key, CancellationToken cancellationToken = default)
    {
        var row = await objects.FirstOrDefaultAsync(new StorageObjectByKeySpec(key, tenantContext.CurrentTenantId, committedOnly: true), cancellationToken).ConfigureAwait(false);
        if (row is null || !InScope(row))
        {
            throw new StorageNotPublicException($"Object '{key}' does not exist; asking for the public URL of a missing object is a caller bug.");
        }

        if (row.Visibility != StorageVisibility.Public)
        {
            throw new StorageNotPublicException($"Object '{key}' is private and has no public URL. Visibility is chosen at write time and is immutable.");
        }

        return provider.GetPublicUrl(StorageScope.PhysicalKey(tenantContext.CurrentTenantId, key, StorageVisibility.Public));
    }
```

Update `GetUploadUrlAsync`'s signature to take `StorageVisibility visibility = StorageVisibility.Private`, use it in its `PhysicalKey` call and its reservation row. `CompleteUploadAsync` needs no signature change — read `row.Visibility` off the pending row for its `PhysicalKey` call.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/Themia.Modules.Storage.IntegrationTests/Themia.Modules.Storage.IntegrationTests.csproj`
Expected: PASS — the new `TenantStorageVisibilityTests` plus the pre-existing `StorageConformanceTests` (which must be unaffected: every private key is unchanged).

- [ ] **Step 7: Update PublicAPI, build clean, commit**

Add to `src/modules/Themia.Modules.Storage/PublicAPI.Unshipped.txt`:

```
Themia.Modules.Storage.ITenantStorage.GetPublicUrlAsync(string! key, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<System.Uri!>!
Themia.Modules.Storage.TenantStorage.GetPublicUrlAsync(string! key, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<System.Uri!>!
Themia.Modules.Storage.StorageNotPublicException
Themia.Modules.Storage.StorageNotPublicException.StorageNotPublicException(string! message) -> void
```

Also **remove** the old `GetUploadUrlAsync` lines from `PublicAPI.Unshipped.txt` and add the new signatures (copy them exactly from the RS0016 diagnostics if they do not match).

```bash
dotnet build Themia.sln --no-incremental
git add src/modules/Themia.Modules.Storage tests/Themia.Modules.Storage.IntegrationTests
git commit -m "feat: tenant-aware public URLs; visibility is immutable once written"
```

---

### Task 8: The Local public route (ungated) + mount-path validation

**Files:**
- Modify: `src/modules/Themia.Modules.Storage/Endpoints/StorageEndpoints.cs`
- Test: `tests/Themia.Modules.Storage.AspNetCore.IntegrationTests/PublicRouteTests.cs` (create; copy the host setup from `AuthorizedGroupRouteTests.cs`, which already builds a host with an auth scheme that never authenticates)

**Interfaces:**
- Consumes: the `transfer` route group (added in 0.8.8 — `StorageEndpoints.cs`), `IStorageProvider.GetAsync`, `StorageKey`.
- Produces: route `GET {prefix}/public/{**key}`.

**This is the finding that would have shipped a broken feature:** the group *returned* by `MapThemiaStorageEndpoints` is the seam adopters gate with `.RequireAuthorization()`. A public route mapped inside it would **401 in an `<img>` tag** — a public URL that fails at render time. It goes in the ungated `transfer` group, which 0.8.8 created for exactly this class of route.

- [ ] **Step 1: Write the failing test**

Create `tests/Themia.Modules.Storage.AspNetCore.IntegrationTests/PublicRouteTests.cs`, copying the host setup from `AuthorizedGroupRouteTests.cs` (same `AlwaysAnonymousHandler`, same `StubUser`), but configuring `UseLocal` with a public container:

```csharp
    .UseLocal(o =>
    {
        o.RootPath = root;
        o.PublicRootPath = publicRoot;
        o.PublicBaseUrl = "http://127.0.0.1/storage/public";
        o.SigningKey = SigningKey;
    });
```

and `app.MapThemiaStorageEndpoints().RequireAuthorization();` — the documented adopter usage. Then:

```csharp
[Fact]
public async Task Public_route_serves_an_unauthenticated_client_when_the_group_requires_auth()
{
    await provider.PutAsync("public/t1/hero.jpg", new MemoryStream(Encoding.UTF8.GetBytes("image-bytes")),
        new StoragePutOptions("image/jpeg", Visibility: StorageVisibility.Public));

    var response = await client.GetAsync("/storage/public/t1/hero.jpg");

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
    Assert.Equal("image-bytes", await response.Content.ReadAsStringAsync());
}

[Fact]
public async Task Public_route_sets_a_cacheable_cache_control()
{
    await provider.PutAsync("public/t1/hero.jpg", new MemoryStream([1]), new StoragePutOptions("image/jpeg", Visibility: StorageVisibility.Public));

    var response = await client.GetAsync("/storage/public/t1/hero.jpg");

    // The deliberate OPPOSITE of the dashboards' no-store: these bytes are not sensitive, and defeating
    // the CDN is the whole failure mode this feature exists to avoid.
    Assert.True(response.Headers.CacheControl!.Public);
}

[Fact]
public async Task Public_route_cannot_reach_a_private_object()
{
    // Same tail, private container. The public route must not serve it under any key shape.
    await provider.PutAsync("t1/secret.pdf", new MemoryStream(Encoding.UTF8.GetBytes("secret")), new StoragePutOptions("application/pdf"));

    var response = await client.GetAsync("/storage/public/t1/secret.pdf");

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
}

[Fact]
public async Task Public_route_rejects_traversal()
{
    var response = await client.GetAsync("/storage/public/..%2F..%2Fblobs%2Ft1%2Fsecret.pdf");

    Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest);
}

[Fact]
public async Task Broker_routes_stay_gated()
{
    var response = await client.GetAsync("/storage/t1/anything.jpg");

    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.Modules.Storage.AspNetCore.IntegrationTests/Themia.Modules.Storage.AspNetCore.IntegrationTests.csproj --filter "FullyQualifiedName~PublicRouteTests"`
Expected: FAIL — 404 (route does not exist).

- [ ] **Step 3: Map the route in the ungated group**

In `src/modules/Themia.Modules.Storage/Endpoints/StorageEndpoints.cs`, add to the **`transfer`** group (never `group`):

```csharp
        // Serve a public object. No auth, no token: a public object is public by definition. It is mapped
        // in the ungated `transfer` group ON PURPOSE — the group returned to the host is the one adopters
        // gate with RequireAuthorization(), and a public URL that 401s in an <img> tag is a URL that looks
        // right and fails at render time. Local only: with S3/R2 the bytes are served straight from the
        // public bucket's custom domain and never reach this app.
        transfer.MapGet("/public/{**key}", async (
            string key,
            [FromServices] IStorageProvider provider,
            [FromServices] StorageModuleOptions options,
            HttpResponse response,
            CancellationToken ct) =>
        {
            // Address the public container explicitly. A private object is unreachable through this route
            // no matter what key is supplied, because the prefix — not the caller — selects the container.
            var physicalKey = StorageKey.PublicPrefix + key;

            StorageReadResult? read;
            try
            {
                read = await provider.GetAsync(physicalKey, ct);
            }
            catch (ArgumentException)
            {
                return Results.NotFound(); // traversal / malformed key
            }
            catch (InvalidOperationException)
            {
                return Results.NotFound(); // no public container configured on this backend
            }

            if (read is null)
            {
                return Results.NotFound();
            }

            response.Headers.CacheControl = $"public, max-age={(int)options.PublicCacheMaxAge.TotalSeconds}";
            return Results.Stream(read.Content, read.ContentType);
        });
```

Add `PublicCacheMaxAge` to `src/modules/Themia.Modules.Storage/StorageModuleOptions.cs`:

```csharp
    /// <summary>How long a public object may be cached by browsers and CDNs (default 1 day). Public media
    /// is not sensitive — the deliberate opposite of the dashboards' <c>no-store</c>.</summary>
    public TimeSpan PublicCacheMaxAge { get; set; } = TimeSpan.FromDays(1);
```

- [ ] **Step 4: Validate the Local mount path**

`LocalStorageOptions.PublicBaseUrl` must be the origin **plus this mount**, or every image 404s. In `MapThemiaStorageEndpoints`, after building the groups, assert it:

```csharp
        // A PublicBaseUrl that is absolute but does not end with this mount (e.g. https://api.example.com
        // instead of https://api.example.com/storage/public) passes the "is it absolute?" check and then
        // 404s on every single image. Fail at startup instead.
        var localOptions = endpoints.ServiceProvider.GetService<LocalStorageOptions>();
        if (localOptions is not null && !string.IsNullOrWhiteSpace(localOptions.PublicBaseUrl) &&
            !localOptions.PublicBaseUrl.TrimEnd('/').EndsWith($"{prefix}/public", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"LocalStorageOptions.PublicBaseUrl ('{localOptions.PublicBaseUrl}') must end with the public route mount " +
                $"('{prefix}/public') — e.g. https://api.example.com{prefix}/public. Otherwise every public URL 404s.");
        }
```

For this to resolve, `StorageBuilder.UseLocal` must register the options object: in `StorageServiceCollectionExtensions.UseLocal`, add `services.TryAddSingleton(local);` before `RegisterProvider(...)`.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Themia.Modules.Storage.AspNetCore.IntegrationTests/Themia.Modules.Storage.AspNetCore.IntegrationTests.csproj`
Expected: PASS — new `PublicRouteTests` plus the existing `LocalSignedRouteTests` and `AuthorizedGroupRouteTests`.

- [ ] **Step 6: Update PublicAPI, build clean, commit**

Add to `src/modules/Themia.Modules.Storage/PublicAPI.Unshipped.txt`:

```
Themia.Modules.Storage.StorageModuleOptions.PublicCacheMaxAge.get -> System.TimeSpan
Themia.Modules.Storage.StorageModuleOptions.PublicCacheMaxAge.set -> void
```

```bash
dotnet build Themia.sln --no-incremental
git add src/modules/Themia.Modules.Storage tests/Themia.Modules.Storage.AspNetCore.IntegrationTests
git commit -m "feat: serve public objects on an ungated Local route with a cacheable Cache-Control"
```

---

### Task 9: Release — version, changelog, migration note, full green

**Files:**
- Modify: `Directory.Build.props`
- Modify: `CHANGELOG.md`
- Modify: `MIGRATION.md`

- [ ] **Step 1: Bump the version**

In `Directory.Build.props`, set `<Version>0.9.0</Version>`.

- [ ] **Step 2: Write the changelog entry**

In `CHANGELOG.md`, directly under `## [Unreleased]`:

```markdown
## [0.9.0] - 2026-07-15

### Added
- **`Themia.Storage` / `Themia.Storage.S3` / `Themia.Modules.Storage`** — permanent, unsigned, **absolute**
  public URLs for world-readable media (coord #0022). `StoragePutOptions.Visibility` selects a container at
  write time; `ITenantStorage.GetPublicUrlAsync(key)` returns the URL, resolved at *read* time from a
  configured absolute `PublicBaseUrl`. A presigned URL is a *time-boxed capability* and is not a substitute:
  an expiring URL breaks OG/Twitter previews on a shared listing (the share is permanent, the URL is not),
  403s a crawler that re-fetches later, and defeats CDN caching because every render mints a fresh cache key.
  Visibility is a property of the **container**, not the object, because R2 has no per-object ACL and S3
  Object Ownership defaults to *bucket owner enforced*, which disables object ACLs entirely — so a per-object
  flag would silently no-op on both real backends. Configure `PublicRootPath` + `PublicBaseUrl` (Local) or
  `PublicBucketName` + `PublicBaseUrl` (S3/R2); a relative `PublicBaseUrl` now throws at **startup**.
  Public objects on Local are served from a new **ungated** `GET {mount}/public/{**key}` route with
  `Cache-Control: public` — deliberately the opposite of the dashboards' `no-store`, because these bytes are
  not sensitive. On S3/R2 they are served straight from the public bucket and never reach the app.

### Changed
- **(breaking)** **`IStorageProvider` gains `Uri GetPublicUrl(string key)`.** Affects only code that
  *implements* the interface directly; every consumer of `ITenantStorage` is unaffected. See `MIGRATION.md`.
- **`ITenantStorage.GetUploadUrlAsync`** takes a `StorageVisibility` (defaulting to `Private`), so a presigned
  upload lands directly in the right container.
- **Visibility is immutable once written.** There is deliberately no move/flip operation: private→public is
  unnecessary (keys are unguessable GUIDs), and public→private is an illusion (a CDN and Google's cache keep
  serving the copy they already have). Because no operation spans two containers, the design has **no
  partial-failure state** — no half-moved object, no orphaned blob, no reconcile sweep. Re-putting an existing
  key with a different `Visibility` throws rather than silently orphaning the old blob or ignoring the caller.
- Private physical keys are **unchanged**, so **no existing blob moves** and no data migration is required.
```

- [ ] **Step 3: Write the migration note**

Append to `MIGRATION.md`:

```markdown
## 0.8.x → 0.9.0

**Breaking: `IStorageProvider.GetPublicUrl(string key)`.** Only affects code that implements
`IStorageProvider` directly — the built-in Local and S3/R2 providers already do. Consumers of
`ITenantStorage` need no change.

If you implement `IStorageProvider`, add:

```csharp
public Uri GetPublicUrl(string key) =>
    throw new InvalidOperationException("This backend has no public container.");
```

**Optional:** to serve public media, configure a public container —
`PublicRootPath` + `PublicBaseUrl` (Local) or `PublicBucketName` + `PublicBaseUrl` (S3/R2) — and write with
`new StoragePutOptions(contentType, Visibility: StorageVisibility.Public)`. A relative `PublicBaseUrl` throws
at startup. **Visibility cannot be changed after a write**; delete and re-upload to move an object.

**Schema:** `storage_objects.visibility` is added by `StorageVisibilityMigration` (FluentMigrator, applied on
boot). Every existing row defaults to `Private`, which is correct: private keys are unchanged and no blob moves.
```

- [ ] **Step 4: Verify the whole solution is green**

Run: `dotnet build Themia.sln --no-incremental`
Expected: `Build succeeded.` with no warnings (RS0016 included).

Run: `dotnet test Themia.sln`
Expected: every suite passes on both `net8.0` and `net10.0`.

- [ ] **Step 5: Commit and open the PR**

```bash
git add Directory.Build.props CHANGELOG.md MIGRATION.md
git commit -m "chore: release 0.9.0 — Themia.Storage public URLs (coord #0022)"
git push -u origin feat/storage-public-url
gh pr create --title "feat: permanent public URLs for Themia.Storage (0.9.0, coord #0022)" --body "Implements docs/superpowers/specs/2026-07-14-storage-public-url-design.md (rev 2). Closes coord #0022."
```

- [ ] **Step 6: Mark the coord request released after the packages publish**

```bash
export COORD_DIR=/Users/sarawut/GitHub/Idevs/single-repo/idevs-coord
"$COORD_DIR/coord" set 0022 released --version 0.9.0 --pr <pr-url> --note "Public URLs shipped; visibility immutable, chosen at write."
```
