# Themia.Modules.Storage (0.5.3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `Themia.Modules.Storage` 0.5.3 — tenant-aware object storage over Local + S3/R2 backends with DB-backed metadata and per-tenant quota.

**Architecture:** Three packages following the established three-layer + per-provider topology: a framework-free neutral core `Themia.Storage` (`IStorageProvider` + Local backend), a neutral `Themia.Storage.S3` (S3 + R2 via configured endpoint), and the net10 `Themia.Modules.Storage` (tenant key-prefix isolation, `storage_objects` metadata + quota over EF/Dapper + FluentMigrator schema, validation/scan seams, DI builder, opt-in endpoints).

**Tech Stack:** .NET 10 (neutral cores multi-target `net8.0;net10.0`), `AWSSDK.S3`, FluentMigrator (via `Themia.Data.Migrations`), EF Core 10 + Dapper (selectable peers), xUnit + Testcontainers (PostgreSQL, SQL Server, MinIO).

**Spec:** [`docs/superpowers/specs/2026-06-17-themia-storage-design.md`](../specs/2026-06-17-themia-storage-design.md)

**Conventions confirmed against the codebase (honor exactly):**
- Neutral packages live in `src/neutral/` and set `<TargetFrameworks>net8.0;net10.0</TargetFrameworks>`; modules live in `src/modules/` and set `<TargetFramework>net10.0</TargetFramework>`. `<Version>` is inherited from `Directory.Build.props` — never set per-csproj.
- Central package management: every dependency version is pinned in `Directory.Packages.props`; `<PackageReference>` carries no `Version`.
- Each shipping package carries `PublicAPI.Shipped.txt` + `PublicAPI.Unshipped.txt` (declared as `<AdditionalFiles>`) and references `Microsoft.CodeAnalysis.PublicApiAnalyzers` per-csproj. A clean build (`dotnet build --no-incremental`) surfaces undocumented public members as `RS0016`; add them to `PublicAPI.Unshipped.txt`.
- `TreatWarningsAsErrors=true` + `GenerateDocumentationFile=true` repo-wide → every public member needs an XML doc comment; warnings fail the build.
- Data abstractions: `IRepository<T, in TKey>` (`T : class`) with `AddAsync`/`Update`/`Remove`/`FirstOrDefaultAsync(spec)`/`AnyAsync(spec)`/`ListAsync(spec)`/`CountAsync(spec)→Task<long>`; `IUnitOfWork.ExecuteInTransactionAsync(Func<CancellationToken,Task>, ct)` + `SaveChangesAsync`; `Specification<T>` with protected `Where(...)` + public `WithoutTenantFilter()`; `IDataFilterScope`; `ITenantEntity { TenantId? TenantId { get; set; } }`; `TenantId` is a validated `readonly record struct` over `string` (in `Themia.Framework.Core.Abstractions.Tenancy`); entity bases `SoftDeletableEntity<TId> : AuditableEntity<TId> : Entity<TId>` (in `Themia.Framework.Core.Abstractions.Entities`); audit fields are `CreatedAt/CreatedBy/LastModifiedAt/LastModifiedBy`; `UniqueConstraintException` in `Themia.Framework.Data.Abstractions.Exceptions`. The framework write path stamps `tenant_id` from the ambient tenant context (services do not set `TenantId` for an in-scope write — mirror `UserService.CreateAsync`).
- Migrations: FluentMigrator only (no `dotnet ef migrations add`). `[Migration(YYYYMMDD####)]`, run via `ThemiaMigrations.Run(MigrationEngine engine, string conn, params Assembly[])`. Filtered-unique-index idiom: portable DDL under `IfDatabase("postgresql","sqlserver").Delegate(...)`, raw `Execute.Sql` filtered indexes under separate `IfDatabase("postgresql")` / `IfDatabase("sqlserver")` delegates, plus an unsupported-engine guard.
- `System.Text.Json` only (never Newtonsoft). `ILogger<T>` only (never `Console.*`). Never log secrets/credentials/presigned URLs/tokens.
- Tests live in `tests/`; add every new project with `dotnet sln Themia.sln add <path>`.

---

## File Structure

**`src/neutral/Themia.Storage/`** (neutral core, `net8.0;net10.0`)
- `Themia.Storage.csproj`, `PublicAPI.Shipped.txt`, `PublicAPI.Unshipped.txt`
- `IStorageProvider.cs` — the backend contract.
- `StorageContracts.cs` — `StoragePutOptions`, `StorageReadResult`, `StorageObjectInfo`, `PresignedUrlRequest`, `PresignedUrlOperation`.
- `Local/LocalStorageOptions.cs`, `Local/LocalStorageProvider.cs`, `Local/LocalUrlSigner.cs`.

**`src/neutral/Themia.Storage.S3/`** (neutral, `net8.0;net10.0`)
- `Themia.Storage.S3.csproj`, `PublicAPI.*`
- `S3StorageOptions.cs`, `S3StorageProvider.cs`.

**`src/modules/Themia.Modules.Storage/`** (module, `net10.0`)
- `Themia.Modules.Storage.csproj`, `PublicAPI.*`
- `Entities/StorageObject.cs`
- `StorageScope.cs` — physical-key prefixing + sanitization.
- `Specifications/StorageSpecs.cs` — `StorageObjectByKeySpec`, `AllStorageObjectsSpec`.
- `Migrations/StorageSchemaMigration.cs`
- `EntityConfiguration/StorageModelConfiguration.cs` — EF config + `ApplyThemiaStorage` extension.
- `Mapping/StorageDapperMappings.cs`
- `Validation/IFileValidator.cs` + `DefaultFileValidator.cs`, `Scanning/IFileScanner.cs` + `NullFileScanner.cs`.
- `StorageModuleOptions.cs`, `StorageExceptions.cs` (`StorageValidationException`, `StorageScanException`, `StorageQuotaExceededException`).
- `ITenantStorage.cs` + `TenantStorage.cs` + `StoredObject.cs`.
- `DependencyInjection/StorageServiceCollectionExtensions.cs` (`AddThemiaStorage` + `StorageBuilder`), `StorageModule.cs`.
- `Endpoints/StorageEndpoints.cs` (`MapThemiaStorageEndpoints`).

**Tests**
- `tests/Themia.Storage.Tests/` — unit (Local provider, signer).
- `tests/Themia.Storage.IntegrationTests/` — provider conformance base + Local subclass + S3/MinIO subclass.
- `tests/Themia.Modules.Storage.Tests/` — unit (StorageScope, validator, options, DI builder).
- `tests/Themia.Modules.Storage.IntegrationTests/` — shared conformance base + fixtures (`IsTestProject=false`).
- `tests/Themia.Modules.Storage.EFCore.IntegrationTests/` + `tests/Themia.Modules.Storage.Dapper.SqlServer.IntegrationTests/` — concrete 4-way subclasses.

---

## Task 1: Pin new dependencies (CPM)

**Files:**
- Modify: `Directory.Packages.props`

- [ ] **Step 1: Add the two new package versions**

In `Directory.Packages.props`, inside the `<ItemGroup>`, add (place near the Testcontainers entries):

```xml
<!-- Storage (Themia.Storage.S3): AWS S3 SDK (also drives Cloudflare R2 via a configured ServiceUrl) -->
<PackageVersion Include="AWSSDK.S3" Version="4.0.6" />
<!-- Storage integration tests: S3-compatible server (MinIO) via Testcontainers (same family as the others) -->
<PackageVersion Include="Testcontainers.Minio" Version="4.12.0" />
```

> Note: pin `AWSSDK.S3` to the current stable 4.x at implementation time if 4.0.6 is unavailable; AWS SDK v4 multi-targets netstandard2.0/net8, valid for both neutral legs.

- [ ] **Step 2: Restore to verify the versions resolve**

Run: `dotnet restore Themia.sln`
Expected: restore succeeds, no `NU1101`/`NU1605` for the new packages.

- [ ] **Step 3: Commit**

```bash
git add Directory.Packages.props
git commit -m "chore(storage): pin AWSSDK.S3 + Testcontainers.Minio for 0.5.3"
```

---

## Task 2: Scaffold `Themia.Storage` core + `IStorageProvider` + contracts

**Files:**
- Create: `src/neutral/Themia.Storage/Themia.Storage.csproj`
- Create: `src/neutral/Themia.Storage/PublicAPI.Shipped.txt`
- Create: `src/neutral/Themia.Storage/PublicAPI.Unshipped.txt`
- Create: `src/neutral/Themia.Storage/StorageContracts.cs`
- Create: `src/neutral/Themia.Storage/IStorageProvider.cs`

- [ ] **Step 1: Create the csproj**

`src/neutral/Themia.Storage/Themia.Storage.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- Neutral cross-framework package: MUST include net8.0 (cross-framework reuse). -->
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <PackageId>Themia.Storage</PackageId>
    <Description>Framework-neutral object-storage abstraction (IStorageProvider) with a Local filesystem backend. Tenant-agnostic, stream-based, opaque keys.</Description>
    <PackageTags>themia;storage;blob;object-storage;filesystem</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.PublicApiAnalyzers">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <AdditionalFiles Include="PublicAPI.Shipped.txt" />
    <AdditionalFiles Include="PublicAPI.Unshipped.txt" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Themia.Storage.Tests" />
    <InternalsVisibleTo Include="Themia.Storage.IntegrationTests" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create the PublicAPI files**

`src/neutral/Themia.Storage/PublicAPI.Shipped.txt`:
```
#nullable enable
```
`src/neutral/Themia.Storage/PublicAPI.Unshipped.txt`:
```
#nullable enable
```

- [ ] **Step 3: Write the contracts**

`src/neutral/Themia.Storage/StorageContracts.cs`:

```csharp
namespace Themia.Storage;

/// <summary>The operation a presigned URL authorizes.</summary>
public enum PresignedUrlOperation
{
    /// <summary>Download (HTTP GET).</summary>
    Get,

    /// <summary>Direct upload (HTTP PUT).</summary>
    Put,
}

/// <summary>Options for writing an object.</summary>
/// <param name="ContentType">The MIME content type to store and serve.</param>
/// <param name="Metadata">Optional provider metadata (small string pairs); may be empty.</param>
/// <param name="Overwrite">Whether to overwrite an existing object at the key (default true).</param>
public readonly record struct StoragePutOptions(
    string ContentType,
    IReadOnlyDictionary<string, string>? Metadata = null,
    bool Overwrite = true);

/// <summary>The result of reading an object: an open content stream plus its metadata.</summary>
/// <param name="Content">The object content stream; the caller disposes it.</param>
/// <param name="ContentType">The stored MIME content type.</param>
/// <param name="Length">The object length in bytes.</param>
public sealed record StorageReadResult(Stream Content, string ContentType, long Length);

/// <summary>Metadata about a stored object (no content).</summary>
/// <param name="Key">The object key.</param>
/// <param name="Length">The object length in bytes.</param>
/// <param name="ContentType">The stored MIME content type.</param>
/// <param name="ETag">The backend entity tag, when available.</param>
public sealed record StorageObjectInfo(string Key, long Length, string ContentType, string? ETag);

/// <summary>A request for a presigned URL.</summary>
/// <param name="Operation">Whether the URL authorizes a download or a direct upload.</param>
/// <param name="Expiry">How long the URL stays valid.</param>
/// <param name="ContentType">The content type the upload must declare (Put only); ignored for Get.</param>
public readonly record struct PresignedUrlRequest(
    PresignedUrlOperation Operation,
    TimeSpan Expiry,
    string? ContentType = null);
```

- [ ] **Step 4: Write the provider interface**

`src/neutral/Themia.Storage/IStorageProvider.cs`:

```csharp
namespace Themia.Storage;

/// <summary>A storage backend over opaque string keys. Tenant-agnostic and framework-free: tenant
/// isolation, metadata, and quota are layered on by <c>Themia.Modules.Storage</c>. Keys are physical
/// keys (already prefixed by the caller); a provider does not interpret them beyond mapping to its
/// own namespace.</summary>
public interface IStorageProvider
{
    /// <summary>Writes <paramref name="content"/> at <paramref name="key"/>.</summary>
    /// <param name="key">The physical object key.</param>
    /// <param name="content">The content stream (read to its end).</param>
    /// <param name="options">Content type and write options.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>Metadata for the written object.</returns>
    Task<StorageObjectInfo> PutAsync(string key, Stream content, StoragePutOptions options, CancellationToken cancellationToken = default);

    /// <summary>Reads the object at <paramref name="key"/>, or <see langword="null"/> when absent.</summary>
    /// <param name="key">The physical object key.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The content stream + metadata, or <see langword="null"/>.</returns>
    Task<StorageReadResult?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Whether an object exists at <paramref name="key"/>.</summary>
    /// <param name="key">The physical object key.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><see langword="true"/> when present.</returns>
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Deletes the object at <paramref name="key"/>. Idempotent — deleting an absent key is a no-op.</summary>
    /// <param name="key">The physical object key.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Issues a presigned URL for a direct client transfer.</summary>
    /// <param name="key">The physical object key.</param>
    /// <param name="request">The operation, expiry, and (for uploads) the required content type.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A time-limited URL.</returns>
    Task<Uri> GetPresignedUrlAsync(string key, PresignedUrlRequest request, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 5: Add to the solution and build**

```bash
dotnet sln Themia.sln add src/neutral/Themia.Storage/Themia.Storage.csproj
dotnet build src/neutral/Themia.Storage/Themia.Storage.csproj --no-incremental
```
Expected: build FAILS with `RS0016` for each new public type/member (undocumented in PublicAPI).

- [ ] **Step 6: Populate PublicAPI.Unshipped.txt from the RS0016 output**

Copy every member the `RS0016` diagnostics name into `src/neutral/Themia.Storage/PublicAPI.Unshipped.txt` (one per line, after the `#nullable enable` header), e.g. `Themia.Storage.IStorageProvider`, `Themia.Storage.IStorageProvider.PutAsync(...) -> ...`, the records' members, the enum members. Re-run the build until it reports **0 warnings / 0 errors**.

Run: `dotnet build src/neutral/Themia.Storage/Themia.Storage.csproj --no-incremental`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 7: Commit**

```bash
git add src/neutral/Themia.Storage Themia.sln
git commit -m "feat(storage): neutral core scaffold — IStorageProvider + contracts"
```

---

## Task 3: `LocalStorageProvider` — Put/Get/Exists/Delete + key sanitization

**Files:**
- Create: `src/neutral/Themia.Storage/Local/LocalStorageOptions.cs`
- Create: `src/neutral/Themia.Storage/Local/LocalStorageProvider.cs`
- Create: `tests/Themia.Storage.Tests/Themia.Storage.Tests.csproj`
- Test: `tests/Themia.Storage.Tests/LocalStorageProviderTests.cs`

- [ ] **Step 1: Create the unit test project**

`tests/Themia.Storage.Tests/Themia.Storage.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/neutral/Themia.Storage/Themia.Storage.csproj" />
  </ItemGroup>
</Project>
```

```bash
dotnet sln Themia.sln add tests/Themia.Storage.Tests/Themia.Storage.Tests.csproj
```

- [ ] **Step 2: Write the failing tests**

`tests/Themia.Storage.Tests/LocalStorageProviderTests.cs`:

```csharp
using System.Text;
using Themia.Storage;
using Themia.Storage.Local;
using Xunit;

namespace Themia.Storage.Tests;

public sealed class LocalStorageProviderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "themia-storage-tests", Guid.NewGuid().ToString("N"));

    private LocalStorageProvider NewProvider() =>
        new(new LocalStorageOptions { RootPath = root, SigningKey = "test-signing-key-please-change" });

    private static MemoryStream Bytes(string s) => new(Encoding.UTF8.GetBytes(s));

    [Fact]
    public async Task Put_then_get_round_trips_content_and_type()
    {
        var provider = NewProvider();
        await provider.PutAsync("a/b.txt", Bytes("hello"), new StoragePutOptions("text/plain"));

        var read = await provider.GetAsync("a/b.txt");

        Assert.NotNull(read);
        Assert.Equal("text/plain", read!.ContentType);
        Assert.Equal(5, read.Length);
        using var reader = new StreamReader(read.Content);
        Assert.Equal("hello", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Get_absent_key_returns_null()
    {
        Assert.Null(await NewProvider().GetAsync("missing.txt"));
    }

    [Fact]
    public async Task Exists_reflects_presence()
    {
        var provider = NewProvider();
        Assert.False(await provider.ExistsAsync("x.txt"));
        await provider.PutAsync("x.txt", Bytes("y"), new StoragePutOptions("text/plain"));
        Assert.True(await provider.ExistsAsync("x.txt"));
    }

    [Fact]
    public async Task Delete_is_idempotent()
    {
        var provider = NewProvider();
        await provider.PutAsync("x.txt", Bytes("y"), new StoragePutOptions("text/plain"));
        await provider.DeleteAsync("x.txt");
        await provider.DeleteAsync("x.txt"); // must not throw
        Assert.False(await provider.ExistsAsync("x.txt"));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("a/../../escape.txt")]
    [InlineData("/abs.txt")]
    public async Task Traversal_keys_are_rejected(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => NewProvider().PutAsync(key, Bytes("x"), new StoragePutOptions("text/plain")));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Themia.Storage.Tests/Themia.Storage.Tests.csproj`
Expected: FAIL — `LocalStorageProvider`/`LocalStorageOptions` do not exist (compile error).

- [ ] **Step 4: Write `LocalStorageOptions`**

`src/neutral/Themia.Storage/Local/LocalStorageOptions.cs`:

```csharp
namespace Themia.Storage.Local;

/// <summary>Options for the filesystem-backed <see cref="LocalStorageProvider"/>.</summary>
public sealed class LocalStorageOptions
{
    /// <summary>The root directory under which objects are stored. Created on first write if absent.</summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>The HMAC key used to sign Local presigned URLs. Must be set to use presigned URLs.</summary>
    public string SigningKey { get; set; } = string.Empty;
}
```

- [ ] **Step 5: Write `LocalStorageProvider` (Put/Get/Exists/Delete; presigned in Task 4)**

`src/neutral/Themia.Storage/Local/LocalStorageProvider.cs`:

```csharp
namespace Themia.Storage.Local;

/// <summary>A filesystem-backed <see cref="IStorageProvider"/>. Maps a sanitized key to a path under
/// <see cref="LocalStorageOptions.RootPath"/>. Intended for development and single-node deployments;
/// production multi-node setups use the S3/R2 backend.</summary>
public sealed class LocalStorageProvider : IStorageProvider
{
    private const string ContentTypeSuffix = ".contenttype";

    private readonly LocalStorageOptions options;

    /// <summary>Creates the provider.</summary>
    /// <param name="options">The filesystem options.</param>
    public LocalStorageProvider(LocalStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RootPath);
        this.options = options;
    }

    /// <inheritdoc />
    public async Task<StorageObjectInfo> PutAsync(string key, Stream content, StoragePutOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var path = ResolvePath(key);
        if (!options.Overwrite && File.Exists(path))
        {
            throw new IOException($"An object already exists at '{key}' and overwrite is disabled.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        long length;
        await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            length = file.Length;
        }

        await File.WriteAllTextAsync(path + ContentTypeSuffix, options.ContentType, cancellationToken).ConfigureAwait(false);
        return new StorageObjectInfo(key, length, options.ContentType, ETag: null);
    }

    /// <inheritdoc />
    public async Task<StorageReadResult?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(key);
        if (!File.Exists(path))
        {
            return null;
        }

        var contentType = File.Exists(path + ContentTypeSuffix)
            ? await File.ReadAllTextAsync(path + ContentTypeSuffix, cancellationToken).ConfigureAwait(false)
            : "application/octet-stream";
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new StorageReadResult(stream, contentType, stream.Length);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(File.Exists(ResolvePath(key)));

    /// <inheritdoc />
    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(key);
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ContentTypeSuffix)) File.Delete(path + ContentTypeSuffix);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<Uri> GetPresignedUrlAsync(string key, PresignedUrlRequest request, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Implemented in Task 4.");

    // Maps a key to an absolute path UNDER RootPath, rejecting traversal/absolute keys by verifying the
    // resolved full path stays within the root.
    private string ResolvePath(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var normalized = key.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Split('/').Contains(".."))
        {
            throw new ArgumentException($"Invalid object key '{key}': absolute paths and '..' segments are not allowed.", nameof(key));
        }

        var rootFull = Path.GetFullPath(options.RootPath);
        var full = Path.GetFullPath(Path.Combine(rootFull, normalized));
        if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !full.Equals(rootFull, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Invalid object key '{key}': resolves outside the storage root.", nameof(key));
        }

        return full;
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Themia.Storage.Tests/Themia.Storage.Tests.csproj`
Expected: PASS (all 7 cases; the 3 traversal `[InlineData]` rows + 4 facts).

- [ ] **Step 7: Update PublicAPI.Unshipped.txt + clean build**

Add `Themia.Storage.Local.LocalStorageOptions`/`LocalStorageProvider` members to `src/neutral/Themia.Storage/PublicAPI.Unshipped.txt` until `dotnet build src/neutral/Themia.Storage/Themia.Storage.csproj --no-incremental` is clean.

- [ ] **Step 8: Commit**

```bash
git add src/neutral/Themia.Storage tests/Themia.Storage.Tests Themia.sln
git commit -m "feat(storage): LocalStorageProvider with key sanitization"
```

---

## Task 4: Local presigned URLs (`LocalUrlSigner`)

**Files:**
- Create: `src/neutral/Themia.Storage/Local/LocalUrlSigner.cs`
- Modify: `src/neutral/Themia.Storage/Local/LocalStorageProvider.cs` (implement `GetPresignedUrlAsync`)
- Test: `tests/Themia.Storage.Tests/LocalUrlSignerTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/Themia.Storage.Tests/LocalUrlSignerTests.cs`:

```csharp
using Themia.Storage.Local;
using Xunit;

namespace Themia.Storage.Tests;

public sealed class LocalUrlSignerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-06-17T00:00:00Z");
    private readonly LocalUrlSigner signer = new("the-signing-key-32-bytes-minimum!");

    [Fact]
    public void Valid_signature_within_expiry_verifies()
    {
        var token = signer.Sign("tenant/a.txt", "get", Now.AddMinutes(10));
        Assert.True(signer.TryVerify("tenant/a.txt", "get", token, Now));
    }

    [Fact]
    public void Expired_signature_fails()
    {
        var token = signer.Sign("tenant/a.txt", "get", Now.AddMinutes(-1));
        Assert.False(signer.TryVerify("tenant/a.txt", "get", token, Now));
    }

    [Fact]
    public void Tampered_key_fails()
    {
        var token = signer.Sign("tenant/a.txt", "get", Now.AddMinutes(10));
        Assert.False(signer.TryVerify("tenant/OTHER.txt", "get", token, Now));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Themia.Storage.Tests/Themia.Storage.Tests.csproj --filter LocalUrlSignerTests`
Expected: FAIL — `LocalUrlSigner` does not exist.

- [ ] **Step 3: Implement `LocalUrlSigner`**

`src/neutral/Themia.Storage/Local/LocalUrlSigner.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace Themia.Storage.Local;

/// <summary>Signs and verifies Local presigned-URL tokens with HMAC-SHA256 over
/// <c>key|operation|expiryUnixSeconds</c>. The module's download/upload endpoint verifies the token
/// before serving, giving the Local backend the same time-limited, tamper-evident URLs as S3/R2.</summary>
public sealed class LocalUrlSigner
{
    private readonly byte[] keyBytes;

    /// <summary>Creates the signer.</summary>
    /// <param name="signingKey">The shared HMAC key (keep secret; never log it).</param>
    public LocalUrlSigner(string signingKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signingKey);
        keyBytes = Encoding.UTF8.GetBytes(signingKey);
    }

    /// <summary>Produces a token authorizing <paramref name="operation"/> on <paramref name="key"/> until
    /// <paramref name="expiresAt"/>, formatted as <c>{expiryUnix}.{base64urlSignature}</c>.</summary>
    /// <param name="key">The physical object key.</param>
    /// <param name="operation">The operation tag (e.g. "get"/"put").</param>
    /// <param name="expiresAt">When the token expires.</param>
    /// <returns>The signed token.</returns>
    public string Sign(string key, string operation, DateTimeOffset expiresAt)
    {
        var expiry = expiresAt.ToUnixTimeSeconds();
        var signature = Compute(key, operation, expiry);
        return $"{expiry}.{signature}";
    }

    /// <summary>Verifies <paramref name="token"/> for <paramref name="key"/>/<paramref name="operation"/>
    /// at <paramref name="now"/> (constant-time compare; rejects malformed or expired tokens).</summary>
    /// <param name="key">The physical object key.</param>
    /// <param name="operation">The operation tag.</param>
    /// <param name="token">The token to verify.</param>
    /// <param name="now">The current time.</param>
    /// <returns><see langword="true"/> when valid and unexpired.</returns>
    public bool TryVerify(string key, string operation, string token, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var dot = token.IndexOf('.');
        if (dot <= 0 || !long.TryParse(token.AsSpan(0, dot), out var expiry)) return false;
        if (DateTimeOffset.FromUnixTimeSeconds(expiry) < now) return false;

        var expected = Compute(key, operation, expiry);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(token[(dot + 1)..]), Encoding.UTF8.GetBytes(expected));
    }

    private string Compute(string key, string operation, long expiry)
    {
        var payload = Encoding.UTF8.GetBytes($"{key}|{operation}|{expiry}");
        var hash = HMACSHA256.HashData(keyBytes, payload);
        return Convert.ToBase64String(hash).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
```

- [ ] **Step 4: Implement `LocalStorageProvider.GetPresignedUrlAsync`**

In `src/neutral/Themia.Storage/Local/LocalStorageProvider.cs`, replace the `throw new NotImplementedException(...)` body of `GetPresignedUrlAsync` with:

```csharp
    /// <inheritdoc />
    public Task<Uri> GetPresignedUrlAsync(string key, PresignedUrlRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SigningKey);
        ResolvePath(key); // validates the key
        var op = request.Operation == PresignedUrlOperation.Put ? "put" : "get";
        var token = new LocalUrlSigner(options.SigningKey).Sign(key, op, DateTimeOffset.UtcNow.Add(request.Expiry));
        // Relative URI the module's MapThemiaStorageEndpoints download/upload route materializes + verifies.
        var encodedKey = Uri.EscapeDataString(key);
        return Task.FromResult(new Uri($"themia-storage://{op}/{encodedKey}?token={Uri.EscapeDataString(token)}", UriKind.Absolute));
    }
```

> Note: the `themia-storage://` scheme is a relative-intent marker; the module endpoint rewrites it to its own route base. Using a `Uri` keeps the `IStorageProvider` contract uniform with S3/R2 (which return absolute https URLs).

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/Themia.Storage.Tests/Themia.Storage.Tests.csproj`
Expected: PASS (signer + provider tests).

- [ ] **Step 6: PublicAPI + clean build + commit**

Add `LocalUrlSigner` members to `PublicAPI.Unshipped.txt`; clean-build `Themia.Storage` to 0/0.

```bash
git add src/neutral/Themia.Storage tests/Themia.Storage.Tests
git commit -m "feat(storage): Local presigned URLs via HMAC signer"
```

---

## Task 5: Scaffold `Themia.Storage.S3` + `S3StorageProvider`

**Files:**
- Create: `src/neutral/Themia.Storage.S3/Themia.Storage.S3.csproj`, `PublicAPI.Shipped.txt`, `PublicAPI.Unshipped.txt`
- Create: `src/neutral/Themia.Storage.S3/S3StorageOptions.cs`
- Create: `src/neutral/Themia.Storage.S3/S3StorageProvider.cs`

- [ ] **Step 1: Create the csproj**

`src/neutral/Themia.Storage.S3/Themia.Storage.S3.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <PackageId>Themia.Storage.S3</PackageId>
    <Description>S3 (and Cloudflare R2, MinIO, any S3-compatible endpoint) backend for Themia.Storage.</Description>
    <PackageTags>themia;storage;s3;r2;minio;object-storage</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Themia.Storage/Themia.Storage.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="AWSSDK.S3" />
    <PackageReference Include="Microsoft.CodeAnalysis.PublicApiAnalyzers">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <AdditionalFiles Include="PublicAPI.Shipped.txt" />
    <AdditionalFiles Include="PublicAPI.Unshipped.txt" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Themia.Storage.IntegrationTests" />
  </ItemGroup>
</Project>
```

PublicAPI files: same `#nullable enable` header as Task 2 Step 2.

- [ ] **Step 2: Write `S3StorageOptions`**

`src/neutral/Themia.Storage.S3/S3StorageOptions.cs`:

```csharp
namespace Themia.Storage.S3;

/// <summary>Options for the S3-compatible <see cref="S3StorageProvider"/> (AWS S3, Cloudflare R2, MinIO).</summary>
public sealed class S3StorageOptions
{
    /// <summary>The bucket objects are stored in.</summary>
    public string BucketName { get; set; } = string.Empty;

    /// <summary>The AWS region system name (e.g. <c>us-east-1</c>). Ignored when <see cref="ServiceUrl"/> is set.</summary>
    public string? Region { get; set; }

    /// <summary>The access key id. When null, the AWS default credential chain is used.</summary>
    public string? AccessKey { get; set; }

    /// <summary>The secret access key. When null, the AWS default credential chain is used.</summary>
    public string? SecretKey { get; set; }

    /// <summary>A custom service endpoint (set for Cloudflare R2 / MinIO / any S3-compatible server).</summary>
    public Uri? ServiceUrl { get; set; }

    /// <summary>Whether to force path-style addressing (required for R2 and MinIO).</summary>
    public bool ForcePathStyle { get; set; }
}
```

- [ ] **Step 3: Write `S3StorageProvider`**

`src/neutral/Themia.Storage.S3/S3StorageProvider.cs`:

```csharp
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Themia.Storage.S3;

/// <summary>An <see cref="IStorageProvider"/> over any S3-compatible backend. Cloudflare R2 and MinIO
/// are supported by setting <see cref="S3StorageOptions.ServiceUrl"/> + <see cref="S3StorageOptions.ForcePathStyle"/>.</summary>
public sealed class S3StorageProvider : IStorageProvider, IDisposable
{
    private readonly IAmazonS3 client;
    private readonly string bucket;

    /// <summary>Creates the provider, building the S3 client from <paramref name="options"/>.</summary>
    /// <param name="options">The S3 options.</param>
    public S3StorageProvider(S3StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BucketName);
        bucket = options.BucketName;
        client = BuildClient(options);
    }

    /// <summary>Creates the provider over an existing client (used by tests against MinIO).</summary>
    /// <param name="client">The S3 client.</param>
    /// <param name="bucketName">The bucket name.</param>
    public S3StorageProvider(IAmazonS3 client, string bucketName)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketName);
        this.client = client;
        bucket = bucketName;
    }

    /// <inheritdoc />
    public async Task<StorageObjectInfo> PutAsync(string key, Stream content, StoragePutOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var response = await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = content,
            ContentType = options.ContentType,
            AutoCloseStream = false,
        }, cancellationToken).ConfigureAwait(false);

        var length = content.CanSeek ? content.Length : 0;
        return new StorageObjectInfo(key, length, options.ContentType, response.ETag);
    }

    /// <inheritdoc />
    public async Task<StorageReadResult?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await client.GetObjectAsync(bucket, key, cancellationToken).ConfigureAwait(false);
            return new StorageReadResult(response.ResponseStream, response.Headers.ContentType ?? "application/octet-stream", response.ContentLength);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            await client.GetObjectMetadataAsync(bucket, key, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
        client.DeleteObjectAsync(bucket, key, cancellationToken);

    /// <inheritdoc />
    public Task<Uri> GetPresignedUrlAsync(string key, PresignedUrlRequest request, CancellationToken cancellationToken = default)
    {
        var url = client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = key,
            Verb = request.Operation == PresignedUrlOperation.Put ? HttpVerb.PUT : HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(request.Expiry),
            ContentType = request.Operation == PresignedUrlOperation.Put ? request.ContentType : null,
        });
        return Task.FromResult(new Uri(url));
    }

    /// <inheritdoc />
    public void Dispose() => client.Dispose();

    private static IAmazonS3 BuildClient(S3StorageOptions options)
    {
        var config = new AmazonS3Config { ForcePathStyle = options.ForcePathStyle };
        if (options.ServiceUrl is not null)
        {
            config.ServiceURL = options.ServiceUrl.AbsoluteUri;
        }
        else if (!string.IsNullOrWhiteSpace(options.Region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }

        return options.AccessKey is not null && options.SecretKey is not null
            ? new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config)
            : new AmazonS3Client(config);
    }
}
```

- [ ] **Step 4: Add to sln, clean build, populate PublicAPI**

```bash
dotnet sln Themia.sln add src/neutral/Themia.Storage.S3/Themia.Storage.S3.csproj
dotnet build src/neutral/Themia.Storage.S3/Themia.Storage.S3.csproj --no-incremental
```
Populate `PublicAPI.Unshipped.txt` from `RS0016` until 0/0.

- [ ] **Step 5: Commit**

```bash
git add src/neutral/Themia.Storage.S3 Themia.sln
git commit -m "feat(storage): S3/R2 backend (Themia.Storage.S3)"
```

---

## Task 6: Provider conformance tests (Local + S3/MinIO)

**Files:**
- Create: `tests/Themia.Storage.IntegrationTests/Themia.Storage.IntegrationTests.csproj`
- Test: `tests/Themia.Storage.IntegrationTests/StorageProviderConformanceTests.cs`
- Test: `tests/Themia.Storage.IntegrationTests/LocalStorageProviderConformanceTests.cs`
- Test: `tests/Themia.Storage.IntegrationTests/S3StorageProviderConformanceTests.cs`

- [ ] **Step 1: Create the test project**

`tests/Themia.Storage.IntegrationTests/Themia.Storage.IntegrationTests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Testcontainers.Minio" />
    <PackageReference Include="AWSSDK.S3" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/neutral/Themia.Storage/Themia.Storage.csproj" />
    <ProjectReference Include="../../src/neutral/Themia.Storage.S3/Themia.Storage.S3.csproj" />
  </ItemGroup>
</Project>
```

```bash
dotnet sln Themia.sln add tests/Themia.Storage.IntegrationTests/Themia.Storage.IntegrationTests.csproj
```

- [ ] **Step 2: Write the conformance base + Local subclass (failing)**

`tests/Themia.Storage.IntegrationTests/StorageProviderConformanceTests.cs`:

```csharp
using System.Text;
using Themia.Storage;
using Xunit;

namespace Themia.Storage.IntegrationTests;

public abstract class StorageProviderConformanceTests
{
    protected abstract IStorageProvider Provider { get; }

    private static MemoryStream Bytes(string s) => new(Encoding.UTF8.GetBytes(s));

    [Fact]
    public async Task Put_then_get_round_trips()
    {
        var key = $"conf/{Guid.NewGuid():N}.txt";
        await Provider.PutAsync(key, Bytes("hello"), new StoragePutOptions("text/plain"));

        var read = await Provider.GetAsync(key);
        Assert.NotNull(read);
        using var reader = new StreamReader(read!.Content);
        Assert.Equal("hello", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Get_absent_returns_null() =>
        Assert.Null(await Provider.GetAsync($"conf/{Guid.NewGuid():N}.txt"));

    [Fact]
    public async Task Exists_and_delete()
    {
        var key = $"conf/{Guid.NewGuid():N}.txt";
        await Provider.PutAsync(key, Bytes("x"), new StoragePutOptions("text/plain"));
        Assert.True(await Provider.ExistsAsync(key));
        await Provider.DeleteAsync(key);
        Assert.False(await Provider.ExistsAsync(key));
    }

    [Fact]
    public async Task Presigned_get_url_is_returned()
    {
        var key = $"conf/{Guid.NewGuid():N}.txt";
        await Provider.PutAsync(key, Bytes("x"), new StoragePutOptions("text/plain"));
        var url = await Provider.GetPresignedUrlAsync(key, new PresignedUrlRequest(PresignedUrlOperation.Get, TimeSpan.FromMinutes(5)));
        Assert.False(string.IsNullOrWhiteSpace(url.ToString()));
    }
}
```

`tests/Themia.Storage.IntegrationTests/LocalStorageProviderConformanceTests.cs`:

```csharp
using Themia.Storage;
using Themia.Storage.Local;
using Xunit;

namespace Themia.Storage.IntegrationTests;

public sealed class LocalStorageProviderConformanceTests : StorageProviderConformanceTests, IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "themia-storage-conf", Guid.NewGuid().ToString("N"));

    protected override IStorageProvider Provider =>
        new LocalStorageProvider(new LocalStorageOptions { RootPath = root, SigningKey = "conf-signing-key-please-change!" });

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
```

- [ ] **Step 3: Run — Local conformance passes**

Run: `dotnet test tests/Themia.Storage.IntegrationTests/Themia.Storage.IntegrationTests.csproj --filter LocalStorageProviderConformanceTests`
Expected: PASS.

- [ ] **Step 4: Write the S3/MinIO subclass**

`tests/Themia.Storage.IntegrationTests/S3StorageProviderConformanceTests.cs`:

```csharp
using Amazon.S3;
using Amazon.S3.Model;
using Testcontainers.Minio;
using Themia.Storage;
using Themia.Storage.S3;
using Xunit;

namespace Themia.Storage.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class S3StorageProviderConformanceTests : StorageProviderConformanceTests, IAsyncLifetime
{
    private readonly MinioContainer container = new MinioBuilder("minio/minio:RELEASE.2025-04-22T22-12-26Z").Build();
    private IAmazonS3 client = null!;
    private S3StorageProvider provider = null!;

    protected override IStorageProvider Provider => provider;

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        var config = new AmazonS3Config
        {
            ServiceURL = container.GetConnectionString(),
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
        };
        client = new AmazonS3Client(container.GetAccessKey(), container.GetSecretKey(), config);
        await client.PutBucketAsync(new PutBucketRequest { BucketName = "themia-conf" });
        provider = new S3StorageProvider(client, "themia-conf");
    }

    public async Task DisposeAsync()
    {
        provider.Dispose();
        await container.DisposeAsync();
    }
}
```

> Note: `MinioContainer.GetConnectionString()` returns the `http://host:port` endpoint; `GetAccessKey()`/`GetSecretKey()` return the container's root credentials. Confirm method names against the installed `Testcontainers.Minio` 4.12.0 at implementation; if the accessors differ, the defaults are `minioadmin`/`minioadmin`.

- [ ] **Step 5: Run — S3/MinIO conformance passes (Docker required)**

Run: `dotnet test tests/Themia.Storage.IntegrationTests/Themia.Storage.IntegrationTests.csproj`
Expected: PASS for both Local and S3/MinIO subclasses.

- [ ] **Step 6: Commit**

```bash
git add tests/Themia.Storage.IntegrationTests Themia.sln
git commit -m "test(storage): provider conformance (Local + S3/MinIO)"
```

---

## Task 7: Scaffold `Themia.Modules.Storage`

**Files:**
- Create: `src/modules/Themia.Modules.Storage/Themia.Modules.Storage.csproj`, `PublicAPI.Shipped.txt`, `PublicAPI.Unshipped.txt`

- [ ] **Step 1: Create the csproj**

`src/modules/Themia.Modules.Storage/Themia.Modules.Storage.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>Themia.Modules.Storage</PackageId>
    <Description>Tenant-aware object storage — key-prefix isolation, DB-backed metadata + per-tenant quota over EF or Dapper, validation/scan seams, and a Local/S3/R2 backend builder. FluentMigrator schema (PostgreSQL + SQL Server).</Description>
    <PackageTags>themia;storage;object-storage;multi-tenancy;s3;r2;efcore;dapper</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../neutral/Themia.Storage/Themia.Storage.csproj" />
    <ProjectReference Include="../../neutral/Themia.Storage.S3/Themia.Storage.S3.csproj" />
    <ProjectReference Include="../../framework/Themia.Framework.Core/Themia.Framework.Core.csproj" />
    <ProjectReference Include="../../framework/Themia.Framework.Data.Abstractions/Themia.Framework.Data.Abstractions.csproj" />
    <ProjectReference Include="../../framework/Themia.Framework.Data.EFCore/Themia.Framework.Data.EFCore.csproj" />
    <ProjectReference Include="../../framework/Themia.Framework.Data.Dapper/Themia.Framework.Data.Dapper.csproj" />
    <ProjectReference Include="../../neutral/Themia.Data.Migrations/Themia.Data.Migrations.csproj" />
  </ItemGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Relational" />
    <PackageReference Include="FluentMigrator" />
    <PackageReference Include="Microsoft.CodeAnalysis.PublicApiAnalyzers">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <AdditionalFiles Include="PublicAPI.Shipped.txt" />
    <AdditionalFiles Include="PublicAPI.Unshipped.txt" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Themia.Modules.Storage.Tests" />
    <InternalsVisibleTo Include="Themia.Modules.Storage.IntegrationTests" />
  </ItemGroup>
</Project>
```

PublicAPI files: `#nullable enable` header only.

- [ ] **Step 2: Add to sln + build (empty, succeeds)**

```bash
dotnet sln Themia.sln add src/modules/Themia.Modules.Storage/Themia.Modules.Storage.csproj
dotnet build src/modules/Themia.Modules.Storage/Themia.Modules.Storage.csproj --no-incremental
```
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)` (no public types yet).

- [ ] **Step 3: Commit**

```bash
git add src/modules/Themia.Modules.Storage Themia.sln
git commit -m "chore(storage): scaffold Themia.Modules.Storage project"
```

---

## Task 8: `StorageObject` entity + `StorageScope` (prefix + sanitization)

**Files:**
- Create: `src/modules/Themia.Modules.Storage/Entities/StorageObject.cs`
- Create: `src/modules/Themia.Modules.Storage/StorageScope.cs`
- Create: `tests/Themia.Modules.Storage.Tests/Themia.Modules.Storage.Tests.csproj`
- Test: `tests/Themia.Modules.Storage.Tests/StorageScopeTests.cs`

- [ ] **Step 1: Create the unit test project**

`tests/Themia.Modules.Storage.Tests/Themia.Modules.Storage.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/modules/Themia.Modules.Storage/Themia.Modules.Storage.csproj" />
  </ItemGroup>
</Project>
```

```bash
dotnet sln Themia.sln add tests/Themia.Modules.Storage.Tests/Themia.Modules.Storage.Tests.csproj
```

- [ ] **Step 2: Write the failing `StorageScope` test**

`tests/Themia.Modules.Storage.Tests/StorageScopeTests.cs`:

```csharp
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Storage;
using Xunit;

namespace Themia.Modules.Storage.Tests;

public sealed class StorageScopeTests
{
    [Fact]
    public void Tenant_key_is_prefixed_with_tenant_id()
    {
        Assert.Equal("acme/a/b.txt", StorageScope.PhysicalKey(new TenantId("acme"), "a/b.txt"));
    }

    [Fact]
    public void Platform_key_uses_the_platform_prefix()
    {
        Assert.Equal("_platform/a/b.txt", StorageScope.PhysicalKey(null, "a/b.txt"));
    }

    [Fact]
    public void Backslashes_are_normalized()
    {
        Assert.Equal("acme/a/b.txt", StorageScope.PhysicalKey(new TenantId("acme"), "a\\b.txt"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/../../escape")]
    [InlineData("/abs")]
    [InlineData("")]
    public void Invalid_keys_are_rejected(string key)
    {
        Assert.Throws<ArgumentException>(() => StorageScope.PhysicalKey(new TenantId("acme"), key));
    }
}
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test tests/Themia.Modules.Storage.Tests/Themia.Modules.Storage.Tests.csproj`
Expected: FAIL — `StorageScope` does not exist.

- [ ] **Step 4: Write `StorageScope`**

`src/modules/Themia.Modules.Storage/StorageScope.cs`:

```csharp
using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Modules.Storage;

/// <summary>Maps a caller's logical key to the physical key handed to <see cref="Themia.Storage.IStorageProvider"/>,
/// prefixing it with the tenant id (or the platform prefix). Centralizing the prefix here is what makes
/// tenant isolation hold by construction — a caller can never reach another tenant's blob.</summary>
public static class StorageScope
{
    /// <summary>The prefix for platform (tenant-less) objects.</summary>
    public const string PlatformPrefix = "_platform";

    /// <summary>Builds the physical key for <paramref name="logicalKey"/> under <paramref name="tenantId"/>.</summary>
    /// <param name="tenantId">The owning tenant, or <see langword="null"/> for a platform object.</param>
    /// <param name="logicalKey">The caller's key (sanitized: no leading '/', no '..' segments).</param>
    /// <returns>The physical key <c>{tenant}/{key}</c> (or <c>_platform/{key}</c>).</returns>
    /// <exception cref="ArgumentException">The key is blank, absolute, or contains a '..' segment.</exception>
    public static string PhysicalKey(TenantId? tenantId, string logicalKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalKey);
        var normalized = logicalKey.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Split('/').Contains(".."))
        {
            throw new ArgumentException($"Invalid object key '{logicalKey}': absolute paths and '..' segments are not allowed.", nameof(logicalKey));
        }

        var prefix = tenantId?.Value ?? PlatformPrefix;
        return $"{prefix}/{normalized}";
    }
}
```

- [ ] **Step 5: Write `StorageObject`**

`src/modules/Themia.Modules.Storage/Entities/StorageObject.cs`:

```csharp
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Modules.Storage.Entities;

/// <summary>Metadata for a stored object. Tenant-scoped when <see cref="ITenantEntity.TenantId"/> is set;
/// a platform object when it is <see langword="null"/>. The blob itself lives in the configured
/// <see cref="Themia.Storage.IStorageProvider"/>; this row is the source of truth for existence and quota.</summary>
public sealed class StorageObject : SoftDeletableEntity<Guid>, ITenantEntity
{
    /// <inheritdoc />
    public TenantId? TenantId { get; set; }

    /// <summary>The logical key (unprefixed), unique within the tenant.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The stored MIME content type.</summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>The object size in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>The backend entity tag, when available.</summary>
    public string? ETag { get; set; }

    /// <summary>Assigns the identifier for a new (transient) object.</summary>
    /// <param name="id">A client-generated identifier.</param>
    public void SetId(Guid id) => Id = id;
}
```

- [ ] **Step 6: Run the tests + clean build + PublicAPI**

Run: `dotnet test tests/Themia.Modules.Storage.Tests/Themia.Modules.Storage.Tests.csproj`
Expected: PASS. Then clean-build the module, add `StorageScope` + `StorageObject` members to `PublicAPI.Unshipped.txt` until 0/0.

- [ ] **Step 7: Commit**

```bash
git add src/modules/Themia.Modules.Storage tests/Themia.Modules.Storage.Tests Themia.sln
git commit -m "feat(storage): StorageObject entity + StorageScope key prefixing"
```

---

## Task 9: Specifications

**Files:**
- Create: `src/modules/Themia.Modules.Storage/Specifications/StorageSpecs.cs`

- [ ] **Step 1: Write the specs**

`src/modules/Themia.Modules.Storage/Specifications/StorageSpecs.cs`:

```csharp
using Themia.Framework.Data.Abstractions.Specifications;
using Themia.Modules.Storage.Entities;

namespace Themia.Modules.Storage.Specifications;

/// <summary>The object with the given logical key within the ambient tenant (the framework tenant filter
/// isolates it).</summary>
internal sealed class StorageObjectByKeySpec : Specification<StorageObject>
{
    public StorageObjectByKeySpec(string key) => Where(o => o.Key == key);
}

/// <summary>All objects in the ambient tenant (soft-deleted rows are excluded by the framework filter).
/// Used to sum current usage for the per-tenant quota check.</summary>
internal sealed class AllStorageObjectsSpec : Specification<StorageObject>
{
    public AllStorageObjectsSpec() => Where(o => o.SizeBytes >= 0);
}
```

> Note: these are `internal` (consumed only inside the module), so they need no PublicAPI entry. `AllStorageObjectsSpec` uses a trivially-true predicate so `ListAsync` returns the tenant's rows under the ambient filter.

- [ ] **Step 2: Build + commit**

```bash
dotnet build src/modules/Themia.Modules.Storage/Themia.Modules.Storage.csproj --no-incremental
git add src/modules/Themia.Modules.Storage
git commit -m "feat(storage): query specifications"
```
Expected: clean build (internal types → no RS0016).

---

## Task 10: EF entity configuration + Dapper mappings

**Files:**
- Create: `src/modules/Themia.Modules.Storage/EntityConfiguration/StorageModelConfiguration.cs`
- Create: `src/modules/Themia.Modules.Storage/Mapping/StorageDapperMappings.cs`

> Mirror the Identity module's `ApplyThemiaIdentity()` EF extension and `IdentityDapperMappings.Apply(EntityMappingRegistry)`. Read `src/modules/Themia.Modules.Identity/EntityConfiguration/` and `src/modules/Themia.Modules.Identity/Mapping/IdentityDapperMappings.cs` first and copy their exact shape (table/column naming, the `EntityMappingRegistry` API, the snake_case column names, soft-delete + audit columns).

- [ ] **Step 1: Write the EF configuration + extension**

`src/modules/Themia.Modules.Storage/EntityConfiguration/StorageModelConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Themia.Modules.Storage.Entities;

namespace Themia.Modules.Storage.EntityConfiguration;

/// <summary>EF Core mapping for <see cref="StorageObject"/> onto <c>storage.storage_objects</c>.</summary>
internal sealed class StorageObjectConfiguration : IEntityTypeConfiguration<StorageObject>
{
    public void Configure(EntityTypeBuilder<StorageObject> builder)
    {
        builder.ToTable("storage_objects", "storage");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).HasColumnName("id");
        builder.Property(o => o.TenantId).HasColumnName("tenant_id").HasMaxLength(100)
            .HasConversion(v => v == null ? null : v.Value.Value, v => Themia.Framework.Core.Abstractions.Tenancy.TenantId.From(v));
        builder.Property(o => o.Key).HasColumnName("key").HasMaxLength(1024).IsRequired();
        builder.Property(o => o.ContentType).HasColumnName("content_type").HasMaxLength(256).IsRequired();
        builder.Property(o => o.SizeBytes).HasColumnName("size_bytes");
        builder.Property(o => o.ETag).HasColumnName("etag").HasMaxLength(256);
        builder.Property(o => o.CreatedAt).HasColumnName("created_at");
        builder.Property(o => o.CreatedBy).HasColumnName("created_by").HasMaxLength(100);
        builder.Property(o => o.LastModifiedAt).HasColumnName("last_modified_at");
        builder.Property(o => o.LastModifiedBy).HasColumnName("last_modified_by").HasMaxLength(100);
        builder.Property(o => o.IsDeleted).HasColumnName("is_deleted");
        builder.Property(o => o.DeletedAt).HasColumnName("deleted_at");
        builder.Property(o => o.DeletedBy).HasColumnName("deleted_by").HasMaxLength(100);
        builder.Property(o => o.RestoredAt).HasColumnName("restored_at");
        builder.Property(o => o.RestoredBy).HasColumnName("restored_by").HasMaxLength(100);
    }
}

/// <summary>Registers the Storage EF mappings on a model builder.</summary>
public static class StorageModelBuilderExtensions
{
    /// <summary>Applies the <see cref="StorageObject"/> configuration. Call from your
    /// <c>ThemiaDbContext.OnModelCreating</c>.</summary>
    /// <param name="modelBuilder">The model builder.</param>
    /// <returns>The same model builder.</returns>
    public static ModelBuilder ApplyThemiaStorage(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfiguration(new StorageObjectConfiguration());
        return modelBuilder;
    }
}
```

> Verify the `TenantId` value-converter idiom against Identity's actual EF config — if Identity uses a shared convention/registered converter for `TenantId?`, reuse that instead of the inline `HasConversion` above (keep it identical to Identity to avoid drift).

- [ ] **Step 2: Write the Dapper mappings**

`src/modules/Themia.Modules.Storage/Mapping/StorageDapperMappings.cs` — mirror `IdentityDapperMappings.Apply(EntityMappingRegistry)` exactly (same `EntityMappingRegistry` type + registration calls), mapping `StorageObject` → `storage.storage_objects` with the snake_case columns above. Write it to match the Identity file's structure verbatim (table name, schema, column map, key column, soft-delete/audit columns).

- [ ] **Step 3: Build + commit**

```bash
dotnet build src/modules/Themia.Modules.Storage/Themia.Modules.Storage.csproj --no-incremental
```
Add the public `StorageModelBuilderExtensions.ApplyThemiaStorage` to `PublicAPI.Unshipped.txt`; clean build to 0/0.

```bash
git add src/modules/Themia.Modules.Storage
git commit -m "feat(storage): EF configuration + Dapper mappings for storage_objects"
```

---

## Task 11: FluentMigrator schema (`storage.storage_objects`)

**Files:**
- Create: `src/modules/Themia.Modules.Storage/Migrations/StorageSchemaMigration.cs`

- [ ] **Step 1: Write the migration**

`src/modules/Themia.Modules.Storage/Migrations/StorageSchemaMigration.cs`:

```csharp
using System;
using FluentMigrator;

namespace Themia.Modules.Storage.Migrations;

/// <summary>Creates the <c>storage</c> schema and the <c>storage_objects</c> table with per-tenant +
/// platform filtered unique indexes on the logical key, on PostgreSQL and SQL Server.</summary>
[Migration(202606170001, "Themia.Storage: create storage schema and storage_objects")]
public sealed class StorageSchemaMigration : Migration
{
    private const string SchemaName = "storage";

    /// <inheritdoc />
    public override void Up()
    {
        IfDatabase("postgresql", "sqlserver").Delegate(CreateSchemaAndTable);
        // The boolean literal differs per engine (PostgreSQL: false, SQL Server bit: 0).
        IfDatabase("postgresql").Delegate(() => CreateFilteredIndexes(SchemaName, "false"));
        IfDatabase("sqlserver").Delegate(() => CreateFilteredIndexes($"[{SchemaName}]", "0"));

        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Storage supports only PostgreSQL and SQL Server. The active database provider " +
                "is not supported; add a migration branch for it."));
    }

    private void CreateSchemaAndTable()
    {
        if (!Schema.Schema(SchemaName).Exists())
        {
            Create.Schema(SchemaName);
        }

        Create.Table("storage_objects").InSchema(SchemaName)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("tenant_id").AsString(100).Nullable()
            .WithColumn("key").AsString(1024).NotNullable()
            .WithColumn("content_type").AsString(256).NotNullable()
            .WithColumn("size_bytes").AsInt64().NotNullable()
            .WithColumn("etag").AsString(256).Nullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable()
            .WithColumn("created_by").AsString(100).Nullable()
            .WithColumn("last_modified_at").AsDateTimeOffset().Nullable()
            .WithColumn("last_modified_by").AsString(100).Nullable()
            .WithColumn("is_deleted").AsBoolean().NotNullable()
            .WithColumn("deleted_at").AsDateTimeOffset().Nullable()
            .WithColumn("deleted_by").AsString(100).Nullable()
            .WithColumn("restored_at").AsDateTimeOffset().Nullable()
            .WithColumn("restored_by").AsString(100).Nullable();

        // Quota scan path: usage is summed per tenant.
        Create.Index("ix_storage_objects_tenant").OnTable("storage_objects").InSchema(SchemaName)
            .OnColumn("tenant_id").Ascending();
    }

    /// <summary>Emits the per-tenant + platform filtered unique indexes on the logical key, excluding
    /// soft-deleted rows so a deleted key can be re-uploaded. <paramref name="schema"/> is pre-escaped
    /// (<c>storage</c> on PostgreSQL, <c>[storage]</c> on SQL Server); <paramref name="falseLiteral"/> is
    /// the engine's boolean-false literal (<c>false</c> on PostgreSQL, <c>0</c> on SQL Server).</summary>
    private void CreateFilteredIndexes(string schema, string falseLiteral)
    {
        Execute.Sql($"CREATE UNIQUE INDEX ux_storage_objects_tenant_key ON {schema}.storage_objects (tenant_id, key) WHERE tenant_id IS NOT NULL AND is_deleted = {falseLiteral};");
        Execute.Sql($"CREATE UNIQUE INDEX ux_storage_objects_platform_key ON {schema}.storage_objects (key) WHERE tenant_id IS NULL AND is_deleted = {falseLiteral};");
    }

    /// <inheritdoc />
    public override void Down() => Delete.Table("storage_objects").InSchema(SchemaName);
}
```

- [ ] **Step 2: Build + commit**

```bash
dotnet build src/modules/Themia.Modules.Storage/Themia.Modules.Storage.csproj --no-incremental
```
Add `StorageSchemaMigration` members to `PublicAPI.Unshipped.txt` (the class + ctor + `Up`/`Down` overrides) until 0/0.

```bash
git add src/modules/Themia.Modules.Storage
git commit -m "feat(storage): FluentMigrator storage schema + filtered unique indexes"
```

---

## Task 12: Options, validation/scan seams, exceptions

**Files:**
- Create: `src/modules/Themia.Modules.Storage/StorageModuleOptions.cs`
- Create: `src/modules/Themia.Modules.Storage/StorageExceptions.cs`
- Create: `src/modules/Themia.Modules.Storage/Validation/IFileValidator.cs`, `Validation/DefaultFileValidator.cs`
- Create: `src/modules/Themia.Modules.Storage/Scanning/IFileScanner.cs`, `Scanning/NullFileScanner.cs`
- Test: `tests/Themia.Modules.Storage.Tests/DefaultFileValidatorTests.cs`

- [ ] **Step 1: Write the failing validator test**

`tests/Themia.Modules.Storage.Tests/DefaultFileValidatorTests.cs`:

```csharp
using Themia.Modules.Storage;
using Themia.Modules.Storage.Validation;
using Xunit;

namespace Themia.Modules.Storage.Tests;

public sealed class DefaultFileValidatorTests
{
    private static DefaultFileValidator Validator(long max = 1000, params string[] allowed) =>
        new(new StorageModuleOptions { MaxObjectSizeBytes = max, AllowedContentTypes = allowed.Length == 0 ? null : allowed });

    [Fact]
    public void Within_size_and_allowed_type_is_valid()
    {
        var result = Validator(1000, "text/plain").Validate("a.txt", "text/plain", 500);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Over_size_is_invalid()
    {
        Assert.False(Validator(100).Validate("a.txt", "text/plain", 101).IsValid);
    }

    [Fact]
    public void Disallowed_type_is_invalid()
    {
        Assert.False(Validator(1000, "image/png").Validate("a.txt", "text/plain", 10).IsValid);
    }

    [Fact]
    public void Null_allowlist_allows_any_type()
    {
        Assert.True(Validator(1000).Validate("a.bin", "application/x-custom", 10).IsValid);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Themia.Modules.Storage.Tests/Themia.Modules.Storage.Tests.csproj --filter DefaultFileValidatorTests`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Write the options**

`src/modules/Themia.Modules.Storage/StorageModuleOptions.cs`:

```csharp
namespace Themia.Modules.Storage;

/// <summary>Options for the Storage module.</summary>
public sealed class StorageModuleOptions
{
    /// <summary>The named connection string used to run the FluentMigrator schema.</summary>
    public string ConnectionStringName { get; set; } = "Default";

    /// <summary>The maximum size of a single object, in bytes (default 100 MiB).</summary>
    public long MaxObjectSizeBytes { get; set; } = 100L * 1024 * 1024;

    /// <summary>The default per-tenant quota, in bytes (default 5 GiB).</summary>
    public long DefaultTenantQuotaBytes { get; set; } = 5L * 1024 * 1024 * 1024;

    /// <summary>An allowlist of accepted content types. <see langword="null"/> or empty allows any type.</summary>
    public IReadOnlyList<string>? AllowedContentTypes { get; set; }

    /// <summary>Validates the options; throws when a value is out of range.</summary>
    /// <exception cref="ArgumentException">A value is invalid.</exception>
    public void Validate()
    {
        if (MaxObjectSizeBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxObjectSizeBytes), MaxObjectSizeBytes, "Must be at least 1 byte.");
        if (DefaultTenantQuotaBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(DefaultTenantQuotaBytes), DefaultTenantQuotaBytes, "Must be at least 1 byte.");
        if (string.IsNullOrWhiteSpace(ConnectionStringName))
            throw new ArgumentException("Must not be null or whitespace.", nameof(ConnectionStringName));
    }
}
```

- [ ] **Step 4: Write the exceptions**

`src/modules/Themia.Modules.Storage/StorageExceptions.cs`:

```csharp
namespace Themia.Modules.Storage;

/// <summary>Thrown when an upload fails content validation (size or content type).</summary>
public sealed class StorageValidationException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">The reason validation failed.</param>
    public StorageValidationException(string message) : base(message) { }
}

/// <summary>Thrown when an uploaded object fails a virus scan.</summary>
public sealed class StorageScanException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">The threat description.</param>
    public StorageScanException(string message) : base(message) { }
}

/// <summary>Thrown when an upload would exceed the tenant's storage quota.</summary>
public sealed class StorageQuotaExceededException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">The quota details.</param>
    public StorageQuotaExceededException(string message) : base(message) { }
}
```

- [ ] **Step 5: Write the validator seam**

`src/modules/Themia.Modules.Storage/Validation/IFileValidator.cs`:

```csharp
namespace Themia.Modules.Storage.Validation;

/// <summary>The result of validating an upload.</summary>
/// <param name="IsValid">Whether the upload is acceptable.</param>
/// <param name="Error">The reason it was rejected, when invalid.</param>
public readonly record struct FileValidationResult(bool IsValid, string? Error)
{
    /// <summary>A successful result.</summary>
    public static FileValidationResult Valid { get; } = new(true, null);

    /// <summary>A failed result with a reason.</summary>
    /// <param name="error">The reason.</param>
    public static FileValidationResult Invalid(string error) => new(false, error);
}

/// <summary>Validates an upload's declared content type and size before it is stored. The default
/// implementation enforces a size cap + content-type allowlist; content sniffing arrives in 0.5.4.</summary>
public interface IFileValidator
{
    /// <summary>Validates an upload.</summary>
    /// <param name="key">The logical key.</param>
    /// <param name="contentType">The declared content type.</param>
    /// <param name="sizeBytes">The object size in bytes.</param>
    /// <returns>The validation result.</returns>
    FileValidationResult Validate(string key, string contentType, long sizeBytes);
}
```

`src/modules/Themia.Modules.Storage/Validation/DefaultFileValidator.cs`:

```csharp
namespace Themia.Modules.Storage.Validation;

/// <summary>The default <see cref="IFileValidator"/>: rejects objects over
/// <see cref="StorageModuleOptions.MaxObjectSizeBytes"/> or outside
/// <see cref="StorageModuleOptions.AllowedContentTypes"/> (when set).</summary>
public sealed class DefaultFileValidator : IFileValidator
{
    private readonly StorageModuleOptions options;

    /// <summary>Creates the validator.</summary>
    /// <param name="options">The module options.</param>
    public DefaultFileValidator(StorageModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    /// <inheritdoc />
    public FileValidationResult Validate(string key, string contentType, long sizeBytes)
    {
        if (sizeBytes > options.MaxObjectSizeBytes)
        {
            return FileValidationResult.Invalid($"Object exceeds the maximum size of {options.MaxObjectSizeBytes} bytes.");
        }

        if (options.AllowedContentTypes is { Count: > 0 } allow &&
            !allow.Contains(contentType, StringComparer.OrdinalIgnoreCase))
        {
            return FileValidationResult.Invalid($"Content type '{contentType}' is not allowed.");
        }

        return FileValidationResult.Valid;
    }
}
```

- [ ] **Step 6: Write the scanner seam**

`src/modules/Themia.Modules.Storage/Scanning/IFileScanner.cs`:

```csharp
namespace Themia.Modules.Storage.Scanning;

/// <summary>The result of scanning an upload.</summary>
/// <param name="IsClean">Whether the content is clean.</param>
/// <param name="Threat">The threat name, when not clean.</param>
public readonly record struct FileScanResult(bool IsClean, string? Threat)
{
    /// <summary>A clean result.</summary>
    public static FileScanResult Clean { get; } = new(true, null);
}

/// <summary>Scans upload content for malware before it is stored. The default is a no-op; a ClamAV
/// implementation arrives in 0.5.4 (Themia.Storage.ClamAV).</summary>
public interface IFileScanner
{
    /// <summary>Scans <paramref name="content"/>. The stream must be readable; implementations restore its
    /// position if they consume it.</summary>
    /// <param name="content">The content to scan.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The scan result.</returns>
    Task<FileScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default);
}
```

`src/modules/Themia.Modules.Storage/Scanning/NullFileScanner.cs`:

```csharp
namespace Themia.Modules.Storage.Scanning;

/// <summary>A pass-through <see cref="IFileScanner"/> (always clean). The default until a real scanner
/// (ClamAV) is registered in 0.5.4.</summary>
public sealed class NullFileScanner : IFileScanner
{
    /// <inheritdoc />
    public Task<FileScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default) =>
        Task.FromResult(FileScanResult.Clean);
}
```

- [ ] **Step 7: Run tests + clean build + PublicAPI + commit**

Run: `dotnet test tests/Themia.Modules.Storage.Tests/Themia.Modules.Storage.Tests.csproj` → PASS.
Clean-build the module; add the new public types/members to `PublicAPI.Unshipped.txt` until 0/0.

```bash
git add src/modules/Themia.Modules.Storage tests/Themia.Modules.Storage.Tests
git commit -m "feat(storage): options, validation + scan seams, typed exceptions"
```

---

## Task 13: `ITenantStorage` + `TenantStorage`

**Files:**
- Create: `src/modules/Themia.Modules.Storage/StoredObject.cs`
- Create: `src/modules/Themia.Modules.Storage/ITenantStorage.cs`
- Create: `src/modules/Themia.Modules.Storage/TenantStorage.cs`

> Behavior is verified by the integration conformance suite in Task 16 (it needs a real repository + UoW + DB). This task implements the service.

- [ ] **Step 1: Write `StoredObject` + `ITenantStorage`**

`src/modules/Themia.Modules.Storage/StoredObject.cs`:

```csharp
namespace Themia.Modules.Storage;

/// <summary>The result of a successful store operation.</summary>
/// <param name="Id">The metadata row id.</param>
/// <param name="Key">The logical key.</param>
/// <param name="SizeBytes">The stored size.</param>
/// <param name="ContentType">The stored content type.</param>
public sealed record StoredObject(Guid Id, string Key, long SizeBytes, string ContentType);
```

`src/modules/Themia.Modules.Storage/ITenantStorage.cs`:

```csharp
using Themia.Storage;

namespace Themia.Modules.Storage;

/// <summary>Tenant-aware object storage. Operates on logical keys (callers never see the physical
/// tenant-prefixed key) and enforces validation, scanning, metadata, and per-tenant quota.</summary>
public interface ITenantStorage
{
    /// <summary>Validates, scans, quota-checks, stores, and records an object.</summary>
    /// <param name="key">The logical key.</param>
    /// <param name="content">The content stream.</param>
    /// <param name="options">Content type and write options.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The stored-object metadata.</returns>
    Task<StoredObject> PutAsync(string key, Stream content, StoragePutOptions options, CancellationToken cancellationToken = default);

    /// <summary>Reads an object by logical key, or <see langword="null"/> when absent in the tenant.</summary>
    /// <param name="key">The logical key.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The content + metadata, or <see langword="null"/>.</returns>
    Task<StorageReadResult?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Whether an object with the logical key exists in the tenant.</summary>
    /// <param name="key">The logical key.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><see langword="true"/> when present.</returns>
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Deletes an object (soft-deletes its metadata, then removes the blob).</summary>
    /// <param name="key">The logical key.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Issues a presigned download URL for the object.</summary>
    /// <param name="key">The logical key.</param>
    /// <param name="expiry">How long the URL stays valid.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A time-limited download URL.</returns>
    Task<Uri> GetDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken cancellationToken = default);

    /// <summary>Issues a presigned upload URL for the object (the client uploads directly to the backend).</summary>
    /// <param name="key">The logical key.</param>
    /// <param name="contentType">The content type the upload must declare.</param>
    /// <param name="expiry">How long the URL stays valid.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A time-limited upload URL.</returns>
    Task<Uri> GetUploadUrlAsync(string key, string contentType, TimeSpan expiry, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: Write `TenantStorage`**

`src/modules/Themia.Modules.Storage/TenantStorage.cs`:

```csharp
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Modules.Storage.Entities;
using Themia.Modules.Storage.Scanning;
using Themia.Modules.Storage.Specifications;
using Themia.Modules.Storage.Validation;
using Themia.Storage;

namespace Themia.Modules.Storage;

/// <summary>Default <see cref="ITenantStorage"/>. Prefixes every key with the ambient tenant
/// (<see cref="StorageScope"/>), validates + scans uploads, enforces per-tenant quota transactionally
/// (metadata-first), and stores the blob via the configured <see cref="IStorageProvider"/>.</summary>
public sealed class TenantStorage : ITenantStorage
{
    private readonly IStorageProvider provider;
    private readonly IRepository<StorageObject, Guid> objects;
    private readonly IUnitOfWork unitOfWork;
    private readonly ITenantContext tenantContext;
    private readonly IFileValidator validator;
    private readonly IFileScanner scanner;
    private readonly StorageModuleOptions options;

    /// <summary>Creates the service.</summary>
    /// <param name="provider">The storage backend.</param>
    /// <param name="objects">The metadata repository.</param>
    /// <param name="unitOfWork">The unit of work.</param>
    /// <param name="tenantContext">The ambient tenant context.</param>
    /// <param name="validator">The upload validator.</param>
    /// <param name="scanner">The upload scanner.</param>
    /// <param name="options">The module options.</param>
    public TenantStorage(
        IStorageProvider provider,
        IRepository<StorageObject, Guid> objects,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext,
        IFileValidator validator,
        IFileScanner scanner,
        StorageModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(tenantContext);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(options);
        this.provider = provider;
        this.objects = objects;
        this.unitOfWork = unitOfWork;
        this.tenantContext = tenantContext;
        this.validator = validator;
        this.scanner = scanner;
        this.options = options;
    }

    /// <inheritdoc />
    public async Task<StoredObject> PutAsync(string key, Stream content, StoragePutOptions putOptions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var physicalKey = StorageScope.PhysicalKey(tenantContext.CurrentTenantId, key);

        // Buffer + measure with a hard cap so the size limit holds even for a non-seekable stream
        // (server-proxied uploads are for small files; large uploads use a presigned PUT).
        var buffer = await BufferWithCapAsync(content, options.MaxObjectSizeBytes, cancellationToken).ConfigureAwait(false);
        var size = buffer.Length;

        var validation = validator.Validate(key, putOptions.ContentType, size);
        if (!validation.IsValid)
        {
            throw new StorageValidationException(validation.Error ?? "Upload failed validation.");
        }

        var scan = await scanner.ScanAsync(buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;
        if (!scan.IsClean)
        {
            throw new StorageScanException(scan.Threat ?? "Upload failed the virus scan.");
        }

        // Metadata-first: reserve the row (quota-checked) in a transaction, then write the blob.
        StorageObject row = null!;
        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var existing = await objects.FirstOrDefaultAsync(new StorageObjectByKeySpec(key), ct).ConfigureAwait(false);
            var existingSize = existing?.SizeBytes ?? 0;
            var all = await objects.ListAsync(new AllStorageObjectsSpec(), ct).ConfigureAwait(false);
            var usage = all.Sum(o => o.SizeBytes) - existingSize;
            if (usage + size > options.DefaultTenantQuotaBytes)
            {
                throw new StorageQuotaExceededException(
                    $"Storing '{key}' ({size} bytes) would exceed the tenant quota of {options.DefaultTenantQuotaBytes} bytes.");
            }

            if (existing is null)
            {
                row = new StorageObject { Key = key, ContentType = putOptions.ContentType, SizeBytes = size };
                row.SetId(Guid.CreateVersion7());
                await objects.AddAsync(row, ct).ConfigureAwait(false);
            }
            else
            {
                existing.ContentType = putOptions.ContentType;
                existing.SizeBytes = size;
                objects.Update(existing);
                row = existing;
            }

            await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        try
        {
            var info = await provider.PutAsync(physicalKey, buffer, putOptions, cancellationToken).ConfigureAwait(false);
            if (info.ETag is not null)
            {
                row.ETag = info.ETag;
                objects.Update(row);
                await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // Compensate: the metadata row committed but the blob write failed — remove the reservation.
            objects.Remove(row);
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }

        return new StoredObject(row.Id, row.Key, row.SizeBytes, row.ContentType);
    }

    /// <inheritdoc />
    public async Task<StorageReadResult?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var row = await objects.FirstOrDefaultAsync(new StorageObjectByKeySpec(key), cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        return await provider.GetAsync(StorageScope.PhysicalKey(tenantContext.CurrentTenantId, key), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
        objects.AnyAsync(new StorageObjectByKeySpec(key), cancellationToken);

    /// <inheritdoc />
    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var row = await objects.FirstOrDefaultAsync(new StorageObjectByKeySpec(key), cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return;
        }

        objects.Remove(row); // soft-delete (StorageObject : ISoftDeletable)
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Best-effort blob delete; an orphaned blob is swept by the 0.5.5 reconcile job.
        await provider.DeleteAsync(StorageScope.PhysicalKey(tenantContext.CurrentTenantId, key), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<Uri> GetDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken cancellationToken = default) =>
        provider.GetPresignedUrlAsync(
            StorageScope.PhysicalKey(tenantContext.CurrentTenantId, key),
            new PresignedUrlRequest(PresignedUrlOperation.Get, expiry),
            cancellationToken);

    /// <inheritdoc />
    public Task<Uri> GetUploadUrlAsync(string key, string contentType, TimeSpan expiry, CancellationToken cancellationToken = default) =>
        provider.GetPresignedUrlAsync(
            StorageScope.PhysicalKey(tenantContext.CurrentTenantId, key),
            new PresignedUrlRequest(PresignedUrlOperation.Put, expiry, contentType),
            cancellationToken);

    private static async Task<MemoryStream> BufferWithCapAsync(Stream source, long cap, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > cap)
            {
                throw new StorageValidationException($"Object exceeds the maximum size of {cap} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        return buffer;
    }
}
```

- [ ] **Step 3: Clean build + PublicAPI + commit**

Clean-build the module; add `StoredObject`, `ITenantStorage`, `TenantStorage` public members to `PublicAPI.Unshipped.txt` until 0/0.

```bash
git add src/modules/Themia.Modules.Storage
git commit -m "feat(storage): ITenantStorage + TenantStorage (metadata-first quota, key prefixing)"
```

---

## Task 14: DI builder + `StorageModule`

**Files:**
- Create: `src/modules/Themia.Modules.Storage/DependencyInjection/StorageServiceCollectionExtensions.cs`
- Create: `src/modules/Themia.Modules.Storage/StorageModule.cs`
- Test: `tests/Themia.Modules.Storage.Tests/StorageBuilderTests.cs`

- [ ] **Step 1: Write the failing builder test**

`tests/Themia.Modules.Storage.Tests/StorageBuilderTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Themia.Modules.Storage;
using Themia.Modules.Storage.DependencyInjection;
using Themia.Storage;
using Themia.Storage.Local;
using Xunit;

namespace Themia.Modules.Storage.Tests;

public sealed class StorageBuilderTests
{
    [Fact]
    public void UseLocal_registers_a_local_provider()
    {
        var services = new ServiceCollection();
        services.AddThemiaStorage().UseLocal(o => { o.RootPath = Path.GetTempPath(); o.SigningKey = "k-please-change-32-bytes-minimum"; });

        using var sp = services.BuildServiceProvider();
        Assert.IsType<LocalStorageProvider>(sp.GetRequiredService<IStorageProvider>());
        // ITenantStorage is registered (scoped); it also needs the data layer to resolve, exercised in
        // the integration conformance suite (Task 16). Here we only assert the backend selection.
        Assert.Contains(services, d => d.ServiceType == typeof(ITenantStorage));
    }

    [Fact]
    public void Registering_two_backends_throws()
    {
        var services = new ServiceCollection();
        var builder = services.AddThemiaStorage();
        builder.UseLocal(o => { o.RootPath = Path.GetTempPath(); o.SigningKey = "k-please-change-32-bytes-minimum"; });

        Assert.Throws<InvalidOperationException>(() =>
            builder.UseS3(o => { o.BucketName = "b"; o.Region = "us-east-1"; }));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Themia.Modules.Storage.Tests/Themia.Modules.Storage.Tests.csproj --filter StorageBuilderTests`
Expected: FAIL — `AddThemiaStorage`/`StorageBuilder` do not exist.

- [ ] **Step 3: Write the DI extensions + builder**

`src/modules/Themia.Modules.Storage/DependencyInjection/StorageServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Modules.Storage.Scanning;
using Themia.Modules.Storage.Validation;
using Themia.Storage;
using Themia.Storage.Local;
using Themia.Storage.S3;

namespace Themia.Modules.Storage.DependencyInjection;

/// <summary>Registers the Themia Storage module: the tenant storage service, validation/scan seams, and
/// a fluent backend builder (<see cref="StorageBuilder.UseLocal"/> / <see cref="StorageBuilder.UseS3"/> /
/// <see cref="StorageBuilder.UseR2"/>).</summary>
public static class StorageServiceCollectionExtensions
{
    /// <summary>Registers the storage services and returns a builder to select exactly one backend.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the module options.</param>
    /// <returns>The storage builder.</returns>
    public static StorageBuilder AddThemiaStorage(this IServiceCollection services, Action<StorageModuleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new StorageModuleOptions();
        configure?.Invoke(options);
        options.Validate();
        services.TryAddSingleton(options);

        services.AddLogging();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IFileValidator, DefaultFileValidator>();
        services.TryAddSingleton<IFileScanner, NullFileScanner>();
        services.TryAddScoped<ITenantStorage, TenantStorage>();

        // Dapper adopters: contribute the StorageObject mapping to the registry they already registered
        // (mirrors AddThemiaIdentityServices.ContributeDapperMappings). No-op when EF is the peer.
        ContributeDapperMappings(services);

        return new StorageBuilder(services);
    }

    // Mirror Identity: scan the collection for the already-registered EntityMappingRegistry singleton
    // instance and apply the Storage mappings to it. No service provider is built.
    private static void ContributeDapperMappings(IServiceCollection services)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(EntityMappingRegistry)
                && services[i].ImplementationInstance is EntityMappingRegistry registry)
            {
                StorageDapperMappings.Apply(registry);
                return;
            }
        }
    }
}

/// <summary>A fluent builder for selecting the storage backend. Exactly one backend may be registered.</summary>
public sealed class StorageBuilder
{
    private readonly IServiceCollection services;

    internal StorageBuilder(IServiceCollection services) => this.services = services;

    /// <summary>The underlying service collection.</summary>
    public IServiceCollection Services => services;

    /// <summary>Uses the Local filesystem backend.</summary>
    /// <param name="configure">Configures the Local options.</param>
    /// <returns>The same builder.</returns>
    public StorageBuilder UseLocal(Action<LocalStorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var local = new LocalStorageOptions();
        configure(local);
        return RegisterProvider(_ => new LocalStorageProvider(local));
    }

    /// <summary>Uses an S3-compatible backend.</summary>
    /// <param name="configure">Configures the S3 options.</param>
    /// <returns>The same builder.</returns>
    public StorageBuilder UseS3(Action<S3StorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var s3 = new S3StorageOptions();
        configure(s3);
        return RegisterProvider(_ => new S3StorageProvider(s3));
    }

    /// <summary>Uses Cloudflare R2 (S3-compatible: sets the R2 endpoint + path-style addressing).</summary>
    /// <param name="configure">Configures the R2 options.</param>
    /// <returns>The same builder.</returns>
    public StorageBuilder UseR2(Action<R2StorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var r2 = new R2StorageOptions();
        configure(r2);
        ArgumentException.ThrowIfNullOrWhiteSpace(r2.AccountId);
        var s3 = new S3StorageOptions
        {
            BucketName = r2.BucketName,
            AccessKey = r2.AccessKey,
            SecretKey = r2.SecretKey,
            ServiceUrl = new Uri($"https://{r2.AccountId}.r2.cloudflarestorage.com"),
            ForcePathStyle = true,
        };
        return RegisterProvider(_ => new S3StorageProvider(s3));
    }

    private StorageBuilder RegisterProvider(Func<IServiceProvider, IStorageProvider> factory)
    {
        if (services.Any(d => d.ServiceType == typeof(IStorageProvider)))
        {
            throw new InvalidOperationException("A storage backend is already registered; configure exactly one of UseLocal/UseS3/UseR2.");
        }

        services.AddSingleton(factory);
        return this;
    }
}

/// <summary>Cloudflare R2 credentials (an S3-compatible backend addressed by account id).</summary>
public sealed class R2StorageOptions
{
    /// <summary>The Cloudflare account id (forms the R2 endpoint host).</summary>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>The R2 bucket name.</summary>
    public string BucketName { get; set; } = string.Empty;

    /// <summary>The R2 access key id.</summary>
    public string? AccessKey { get; set; }

    /// <summary>The R2 secret access key.</summary>
    public string? SecretKey { get; set; }
}
```

> The `ContributeDapperMappings` helper references `EntityMappingRegistry` (Dapper data layer) and `StorageDapperMappings` (Task 10). Add the same `using` for `EntityMappingRegistry` that `src/modules/Themia.Modules.Identity/DependencyInjection/IdentityServiceCollectionExtensions.cs` uses, plus `using Themia.Modules.Storage.Mapping;`.

- [ ] **Step 4: Write `StorageModule`**

`src/modules/Themia.Modules.Storage/StorageModule.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Data.Migrations;
using Themia.Framework.Core.Modules;
using Themia.Modules.Storage.Migrations;

namespace Themia.Modules.Storage;

/// <summary>The <see cref="IThemiaModule"/> for Storage. <see cref="InitializeAsync"/> runs the
/// FluentMigrator schema. The host wires the service + backend via
/// <c>AddThemiaStorage(...).UseLocal/UseS3/UseR2(...)</c> (the tested entry point); this module exists
/// for hosts that drive modules through the <see cref="IThemiaModule"/> convention.</summary>
public sealed class StorageModule : ThemiaModuleBase
{
    private readonly MigrationEngine engine;
    private readonly StorageModuleOptions options;

    /// <summary>Creates the module.</summary>
    /// <param name="engine">The migration engine for the schema.</param>
    public StorageModule(MigrationEngine engine) : this(engine, new StorageModuleOptions()) { }

    /// <summary>Creates the module with explicit options.</summary>
    /// <param name="engine">The migration engine for the schema.</param>
    /// <param name="options">The module options.</param>
    public StorageModule(MigrationEngine engine, StorageModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.engine = engine;
        this.options = options;
    }

    /// <inheritdoc />
    public override ModuleDescriptor Descriptor { get; } = new(
        name: "Themia.Storage",
        displayName: "Storage",
        description: "Tenant-aware object storage over Local/S3/R2 with DB-backed metadata and per-tenant quota.",
        version: new Version(0, 5, 3, 0));

    /// <inheritdoc />
    public override ValueTask InitializeAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        cancellationToken.ThrowIfCancellationRequested();

        using var scope = serviceProvider.CreateScope();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var connectionString = configuration.GetConnectionString(options.ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{options.ConnectionStringName}' was not found; the storage module requires it.");

        ThemiaMigrations.Run(engine, connectionString, typeof(StorageSchemaMigration).Assembly);
        return ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 5: Run tests + clean build + PublicAPI + commit**

Run: `dotnet test tests/Themia.Modules.Storage.Tests/Themia.Modules.Storage.Tests.csproj` → PASS.
Clean-build; add the DI extension, `StorageBuilder`, `R2StorageOptions`, `StorageModule` public members to `PublicAPI.Unshipped.txt` until 0/0.

```bash
git add src/modules/Themia.Modules.Storage tests/Themia.Modules.Storage.Tests
git commit -m "feat(storage): DI builder (UseLocal/UseS3/UseR2) + StorageModule"
```

---

## Task 15: Opt-in endpoints (`MapThemiaStorageEndpoints`)

**Files:**
- Create: `src/modules/Themia.Modules.Storage/Endpoints/StorageEndpoints.cs`

> Thin, opt-in (mirrors `MapIdentityAuthEndpoints`). Read `src/modules/Themia.Modules.Identity.AspNetCore/Endpoints/IdentityExternalAuthEndpoints.cs` first to match the minimal-API idiom (route group, `Results.*`, request records, validation guard → `ProblemDetails`/400).

- [ ] **Step 1: Write the endpoints**

`src/modules/Themia.Modules.Storage/Endpoints/StorageEndpoints.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Themia.Modules.Storage.Endpoints;

/// <summary>Opt-in minimal-API endpoints for storage. The default flow is presigned direct transfer
/// (the server brokers URLs; the client transfers bytes directly to the backend).</summary>
public static class StorageEndpoints
{
    /// <summary>Maps the storage endpoints onto <paramref name="endpoints"/> under <paramref name="prefix"/>.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="prefix">The route prefix (default <c>/storage</c>).</param>
    /// <returns>The route group builder for further configuration (e.g. <c>.RequireAuthorization()</c>).</returns>
    public static RouteGroupBuilder MapThemiaStorageEndpoints(this IEndpointRouteBuilder endpoints, string prefix = "/storage")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup(prefix);

        // Request a presigned upload URL.
        group.MapPost("/upload-url", async (UploadUrlRequest request, ITenantStorage storage, CancellationToken ct) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Key) || string.IsNullOrWhiteSpace(request.ContentType))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["request"] = ["Key and contentType are required."],
                });
            }

            var url = await storage.GetUploadUrlAsync(request.Key, request.ContentType, TimeSpan.FromMinutes(15), ct);
            return Results.Ok(new { uploadUrl = url.ToString() });
        });

        // Request a presigned download URL (404 when absent).
        group.MapGet("/{*key}", async (string key, ITenantStorage storage, CancellationToken ct) =>
        {
            if (!await storage.ExistsAsync(key, ct))
            {
                return Results.NotFound();
            }

            var url = await storage.GetDownloadUrlAsync(key, TimeSpan.FromMinutes(15), ct);
            return Results.Ok(new { downloadUrl = url.ToString() });
        });

        // Delete an object.
        group.MapDelete("/{*key}", async (string key, ITenantStorage storage, CancellationToken ct) =>
        {
            await storage.DeleteAsync(key, ct);
            return Results.NoContent();
        });

        return group;
    }

    /// <summary>The request body for an upload-URL request.</summary>
    /// <param name="Key">The logical key.</param>
    /// <param name="ContentType">The content type the upload will declare.</param>
    public sealed record UploadUrlRequest(string Key, string ContentType);
}
```

- [ ] **Step 2: Clean build + PublicAPI + commit**

Clean-build; add `StorageEndpoints` + `UploadUrlRequest` public members to `PublicAPI.Unshipped.txt` until 0/0.

```bash
git add src/modules/Themia.Modules.Storage
git commit -m "feat(storage): opt-in MapThemiaStorageEndpoints (presigned-direct flow)"
```

---

## Task 16: Integration conformance (4-way: EF×Dapper × PG×SQL Server)

**Files:**
- Create: `tests/Themia.Modules.Storage.IntegrationTests/Themia.Modules.Storage.IntegrationTests.csproj` (`IsTestProject=false`)
- Create: `tests/Themia.Modules.Storage.IntegrationTests/TestStorageDbContext.cs`
- Create: `tests/Themia.Modules.Storage.IntegrationTests/Fixtures/PostgresStorageFixture.cs`, `Fixtures/SqlServerStorageFixture.cs`
- Create: `tests/Themia.Modules.Storage.IntegrationTests/StorageConformanceTests.cs`
- Create: `tests/Themia.Modules.Storage.EFCore.IntegrationTests/` (csproj + EF subclasses)
- Create: `tests/Themia.Modules.Storage.Dapper.SqlServer.IntegrationTests/` (csproj + Dapper subclass)

> Mirror the Identity integration harness exactly: read `tests/Themia.Modules.Identity.IntegrationTests/Fixtures/PostgresIdentityFixture.cs`, `.../IdentityStoreConformanceTests.cs`, `.../TestIdentityDbContext.cs`, and the concrete `EfPostgresIdentityTests`/`DapperSqlServerIdentityTests` first; copy their structure. Backend for these tests is **Local** (a temp dir) — the metadata/quota/isolation logic is what's under test; the blob backend is covered by Task 6.

- [ ] **Step 1: Create the shared base project**

`tests/Themia.Modules.Storage.IntegrationTests/Themia.Modules.Storage.IntegrationTests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <IsPackable>false</IsPackable>
    <IsTestProject>false</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit" />
    <PackageReference Include="Testcontainers.PostgreSql" />
    <PackageReference Include="Testcontainers.MsSql" />
    <PackageReference Include="Npgsql" />
    <PackageReference Include="Microsoft.Data.SqlClient" />
    <PackageReference Include="Microsoft.Extensions.Configuration" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.Extensions.Logging" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/modules/Themia.Modules.Storage/Themia.Modules.Storage.csproj" />
    <ProjectReference Include="../../src/framework/Themia.Framework.Data.EFCore.PostgreSql/Themia.Framework.Data.EFCore.PostgreSql.csproj" />
    <ProjectReference Include="../../src/framework/Themia.Framework.Data.EFCore.SqlServer/Themia.Framework.Data.EFCore.SqlServer.csproj" />
    <ProjectReference Include="../../src/framework/Themia.Framework.Data.Dapper.PostgreSql/Themia.Framework.Data.Dapper.PostgreSql.csproj" />
    <ProjectReference Include="../../src/framework/Themia.Framework.Data.Dapper.SqlServer/Themia.Framework.Data.Dapper.SqlServer.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write `TestStorageDbContext`**

Mirror `TestIdentityDbContext`: subclass `ThemiaDbContext`, override `CurrentUserId` from a stub accessor, and call `modelBuilder.ApplyThemiaStorage()` in `OnModelCreating`. (Copy the exact base ctor signature from `TestIdentityDbContext.cs`.)

- [ ] **Step 3: Write the fixtures**

`tests/Themia.Modules.Storage.IntegrationTests/Fixtures/PostgresStorageFixture.cs` — copy `PostgresIdentityFixture` verbatim, changing: database name `themia_storage_tests`; migration assembly `typeof(StorageSchemaMigration).Assembly`; reset SQL `TRUNCATE storage.storage_objects RESTART IDENTITY CASCADE;`.

`tests/Themia.Modules.Storage.IntegrationTests/Fixtures/SqlServerStorageFixture.cs` — copy `SqlServerIdentityFixture` verbatim, changing the migration assembly and reset SQL to `DELETE FROM storage.storage_objects;` (the `storage` schema needs no brackets — it is not a reserved word).

- [ ] **Step 4: Write the conformance base (failing)**

`tests/Themia.Modules.Storage.IntegrationTests/StorageConformanceTests.cs`:

```csharp
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Auditing;
using Themia.Modules.Storage;
using Themia.Modules.Storage.DependencyInjection;
using Themia.Storage;
using Xunit;

namespace Themia.Modules.Storage.IntegrationTests;

file sealed class StubCurrentUserAccessor(string? userId) : ICurrentUserAccessor
{
    public string? UserId { get; } = userId;
}

public abstract class StorageConformanceTests
{
    protected abstract void ConfigurePeer(IServiceCollection services, IConfiguration configuration);
    protected abstract Task ResetAsync();
    protected abstract string ConnectionString { get; }

    private static MemoryStream Bytes(string s) => new(Encoding.UTF8.GetBytes(s));

    protected sealed record Scope(ServiceProvider Provider, AsyncServiceScope Inner) : IAsyncDisposable
    {
        public ITenantStorage Storage => Inner.ServiceProvider.GetRequiredService<ITenantStorage>();
        public async ValueTask DisposeAsync() { await Inner.DisposeAsync(); await Provider.DisposeAsync(); }
    }

    protected Scope NewScope(TenantId? tenant, long quota = 1_000_000, string localRoot = "")
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = ConnectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddScoped<ITenantContext>(_ => new TenantContext(tenant));
        ConfigurePeer(services, configuration);
        services.AddThemiaStorage(o => o.DefaultTenantQuotaBytes = quota)
            .UseLocal(o =>
            {
                o.RootPath = string.IsNullOrEmpty(localRoot)
                    ? Path.Combine(Path.GetTempPath(), "themia-storage-it", Guid.NewGuid().ToString("N"))
                    : localRoot;
                o.SigningKey = "integration-signing-key-please-change!";
            });
        services.RemoveAll<ICurrentUserAccessor>();
        services.AddSingleton<ICurrentUserAccessor>(new StubCurrentUserAccessor("test-user"));

        var provider = services.BuildServiceProvider();
        return new Scope(provider, provider.CreateAsyncScope());
    }

    [Fact]
    public async Task Put_get_delete_round_trip()
    {
        await ResetAsync();
        await using var s = NewScope(new TenantId("acme"));
        var stored = await s.Storage.PutAsync("docs/a.txt", Bytes("hello"), new StoragePutOptions("text/plain"));
        Assert.Equal("docs/a.txt", stored.Key);
        Assert.Equal(5, stored.SizeBytes);

        var read = await s.Storage.GetAsync("docs/a.txt");
        Assert.NotNull(read);
        using (var reader = new StreamReader(read!.Content)) Assert.Equal("hello", await reader.ReadToEndAsync());

        await s.Storage.DeleteAsync("docs/a.txt");
        Assert.False(await s.Storage.ExistsAsync("docs/a.txt"));
        Assert.Null(await s.Storage.GetAsync("docs/a.txt"));
    }

    [Fact]
    public async Task Objects_are_tenant_isolated()
    {
        await ResetAsync();
        var sharedRoot = Path.Combine(Path.GetTempPath(), "themia-storage-it", Guid.NewGuid().ToString("N"));
        await using (var a = NewScope(new TenantId("a"), localRoot: sharedRoot))
        {
            await a.Storage.PutAsync("k.txt", Bytes("a-data"), new StoragePutOptions("text/plain"));
        }
        await using (var b = NewScope(new TenantId("b"), localRoot: sharedRoot))
        {
            Assert.False(await b.Storage.ExistsAsync("k.txt"));      // same key, different tenant → invisible
            Assert.Null(await b.Storage.GetAsync("k.txt"));
        }
    }

    [Fact]
    public async Task Quota_is_enforced()
    {
        await ResetAsync();
        await using var s = NewScope(new TenantId("acme"), quota: 8);
        await s.Storage.PutAsync("a.txt", Bytes("12345"), new StoragePutOptions("text/plain")); // 5 bytes, ok
        await Assert.ThrowsAsync<StorageQuotaExceededException>(
            () => s.Storage.PutAsync("b.txt", Bytes("12345"), new StoragePutOptions("text/plain"))); // +5 > 8
    }

    [Fact]
    public async Task Overwrite_replaces_size_not_doubles_usage()
    {
        await ResetAsync();
        await using var s = NewScope(new TenantId("acme"), quota: 8);
        await s.Storage.PutAsync("a.txt", Bytes("12345"), new StoragePutOptions("text/plain")); // 5
        var stored = await s.Storage.PutAsync("a.txt", Bytes("123"), new StoragePutOptions("text/plain")); // replace → 3
        Assert.Equal(3, stored.SizeBytes); // usage 3, not 8
    }

    [Fact]
    public async Task Platform_object_round_trips()
    {
        await ResetAsync();
        await using var s = NewScope(tenant: null);
        await s.Storage.PutAsync("p.txt", Bytes("plat"), new StoragePutOptions("text/plain"));
        Assert.True(await s.Storage.ExistsAsync("p.txt"));
    }

    [Fact]
    public async Task Reupload_after_delete_same_key_succeeds()
    {
        // The filtered unique index excludes soft-deleted rows, so a deleted key can be re-uploaded
        // without hitting the (tenant_id, key) unique constraint.
        await ResetAsync();
        await using var s = NewScope(new TenantId("acme"));
        await s.Storage.PutAsync("k.txt", Bytes("v1"), new StoragePutOptions("text/plain"));
        await s.Storage.DeleteAsync("k.txt");

        var stored = await s.Storage.PutAsync("k.txt", Bytes("v2-longer"), new StoragePutOptions("text/plain"));
        Assert.Equal("k.txt", stored.Key);
        var read = await s.Storage.GetAsync("k.txt");
        Assert.NotNull(read);
        using var reader = new StreamReader(read!.Content);
        Assert.Equal("v2-longer", await reader.ReadToEndAsync());
    }
}
```

- [ ] **Step 5: Create the concrete EF + Dapper subclass projects**

`tests/Themia.Modules.Storage.EFCore.IntegrationTests/Themia.Modules.Storage.EFCore.IntegrationTests.csproj` (`IsTestProject=true`): package refs `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`, `Microsoft.Extensions.Configuration`, `Microsoft.Extensions.DependencyInjection`; project refs the shared base + `Themia.Framework.Data.EFCore.PostgreSql` + `.EFCore.SqlServer` + `.Dapper.PostgreSql`.

Subclasses (mirror `EfPostgresIdentityTests`):

```csharp
// EfPostgresStorageTests.cs
[Trait("Category", "Integration")]
public sealed class EfPostgresStorageTests(PostgresStorageFixture fixture)
    : StorageConformanceTests, IClassFixture<PostgresStorageFixture>
{
    protected override string ConnectionString => fixture.ConnectionString;
    protected override Task ResetAsync() => fixture.ResetAsync();
    protected override void ConfigurePeer(IServiceCollection services, IConfiguration configuration)
    {
        services.AddThemiaPostgres<TestStorageDbContext>(configuration);
        services.AddThemiaDataRepositories<TestStorageDbContext>();
    }
}
```

Add `EfSqlServerStorageTests` (`AddThemiaSqlServer<TestStorageDbContext>` + `SqlServerStorageFixture`) and `DapperPostgresStorageTests` (`AddThemiaDapperPostgres(configuration)` + `PostgresStorageFixture`) in the same project, mirroring the Identity equivalents.

`tests/Themia.Modules.Storage.Dapper.SqlServer.IntegrationTests/` (`IsTestProject=true`): mirror `DapperSqlServerIdentityTests` — one subclass calling `services.AddThemiaDapperSqlServer(configuration)` over `SqlServerStorageFixture`.

- [ ] **Step 6: Add all projects to the sln and run (Docker required)**

```bash
dotnet sln Themia.sln add tests/Themia.Modules.Storage.IntegrationTests/Themia.Modules.Storage.IntegrationTests.csproj
dotnet sln Themia.sln add tests/Themia.Modules.Storage.EFCore.IntegrationTests/Themia.Modules.Storage.EFCore.IntegrationTests.csproj
dotnet sln Themia.sln add tests/Themia.Modules.Storage.Dapper.SqlServer.IntegrationTests/Themia.Modules.Storage.Dapper.SqlServer.IntegrationTests.csproj
dotnet test tests/Themia.Modules.Storage.EFCore.IntegrationTests/Themia.Modules.Storage.EFCore.IntegrationTests.csproj
dotnet test tests/Themia.Modules.Storage.Dapper.SqlServer.IntegrationTests/Themia.Modules.Storage.Dapper.SqlServer.IntegrationTests.csproj
```
Expected: PASS on both peers across both engines (EF Postgres+SqlServer+Dapper Postgres in the EFCore project; Dapper SqlServer in its own).

- [ ] **Step 7: Commit**

```bash
git add tests/Themia.Modules.Storage.IntegrationTests tests/Themia.Modules.Storage.EFCore.IntegrationTests tests/Themia.Modules.Storage.Dapper.SqlServer.IntegrationTests Themia.sln
git commit -m "test(storage): 4-way conformance (metadata, quota, tenant isolation)"
```

---

## Task 17: Release wiring — version, docs, full solution gate

**Files:**
- Modify: `Directory.Build.props` (`<Version>0.5.2</Version>` → `0.5.3`)
- Modify: `CHANGELOG.md`
- Modify: `README.md`
- Modify: `docs/themia-architecture-overview.md`

- [ ] **Step 1: Bump the version**

In `Directory.Build.props`, change `<Version>0.5.2</Version>` to `<Version>0.5.3</Version>`.

- [ ] **Step 2: CHANGELOG entry**

Add a `## [0.5.3] - <date>` section to `CHANGELOG.md` (above `## [0.5.2]`) with **Added** (the three packages: `Themia.Storage` neutral core + Local backend; `Themia.Storage.S3` S3/R2 backend; `Themia.Modules.Storage` tenant-aware service, `storage.storage_objects` FM schema, EF+Dapper peers, validation/scan seams, `AddThemiaStorage().UseLocal/UseS3/UseR2`, opt-in `MapThemiaStorageEndpoints`) and **Security** (tenant key-prefix isolation by construction; size + content-type validation; presigned-direct transfer; secrets never logged).

- [ ] **Step 3: README + architecture overview**

Add a short Storage section to `README.md` (after the Identity section). In `docs/themia-architecture-overview.md`: flip the `Themia.Modules.Storage` catalog row (line ~117) from `⬜ P1 — next to spec` to `✅ built (0.5.3 — …)`, mark Storage ✅ in the Phase-1 list (line ~261), and add the spec/plan to the doc index.

- [ ] **Step 4: Full clean solution build + full test gate**

```bash
dotnet build Themia.sln --no-incremental
```
Expected: `0 Warning(s) 0 Error(s)` (all PublicAPI surfaces documented; TWAE clean).

```bash
dotnet test tests/Themia.Storage.Tests/Themia.Storage.Tests.csproj
dotnet test tests/Themia.Modules.Storage.Tests/Themia.Modules.Storage.Tests.csproj
dotnet test tests/Themia.Storage.IntegrationTests/Themia.Storage.IntegrationTests.csproj
dotnet test tests/Themia.Modules.Storage.EFCore.IntegrationTests/Themia.Modules.Storage.EFCore.IntegrationTests.csproj
dotnet test tests/Themia.Modules.Storage.Dapper.SqlServer.IntegrationTests/Themia.Modules.Storage.Dapper.SqlServer.IntegrationTests.csproj
```
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add Directory.Build.props CHANGELOG.md README.md docs/themia-architecture-overview.md
git commit -m "docs(storage): 0.5.3 — version bump, CHANGELOG, README, architecture status"
```

- [ ] **Step 6: Finish the branch**

Use **superpowers:finishing-a-development-branch** (open the PR for `feat/storage-0.5.3`). The PR description should summarize the three packages, the 4-way conformance + MinIO provider tests, and link the spec + this plan.

---

## Notes for the implementer

- **PublicAPI discipline:** after every task that adds public surface, run `dotnet build <csproj> --no-incremental`, read the `RS0016` diagnostics, and paste each named member into that package's `PublicAPI.Unshipped.txt`. The build is the source of truth.
- **Docker:** Tasks 6 and 16 need Docker running (Testcontainers MinIO / PostgreSQL / SQL Server). The unit tasks (3, 4, 8, 12, 14) do not.
- **Don't set `TenantId` on a new `StorageObject`** — the framework write path stamps `tenant_id` from the ambient tenant context (mirrors `UserService.CreateAsync`). `StorageScope` uses the tenant id only for the physical blob key.
- **Quota is transactional, not strictly serialized** — under heavy concurrency a small overshoot is possible (spec §5.2); strict serialization is the 0.5.5 slice. Don't add locking here.
- **Mirror Identity for the EF config, Dapper mappings, `TestStorageDbContext`, fixtures, and conformance subclasses** — read those files and copy their exact shape rather than inventing a new pattern.
