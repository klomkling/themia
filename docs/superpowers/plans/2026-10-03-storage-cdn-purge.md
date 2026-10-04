# Storage CDN Purge Implementation Plan

> **Status: executed.** Implemented in #276 (merged) and released in 0.30.3. The code and tests below are the plan
> as written; the shipped code differs in these places, so read the code, the package README and the CHANGELOG for
> the final behaviour:
> - `PurgingStorageProvider` normalises the key in **every** member (the plan normalised only in `DeleteAsync`) and
>   reports **any** purge-step failure after a successful delete as `CdnPurgeException`, keeping the caller's own
>   cancellation as `OperationCanceledException` (both found in review).
> - `ApiToken` validation also rejects whitespace and control characters; the `Authorization` redaction is a
>   `PostConfigure` (so a later `ConfigureAll` cannot turn it off); the README notes wrap-once, two S3 slots,
>   app-wide HttpClient defaults, and that only a delete purges.
> - Task 4's characterisation test lives in `TenantStorageVisibilityTests` and drives the real
>   `PurgingStorageProvider` (the plan's stand-in was dropped), and `Themia.Modules.Storage` has two small changes the
>   plan said it would not: the over-quota blob discard runs after the transaction ends, and a refused CDN purge is
>   logged as such. Task 4's run target was also wrong in the plan (the abstract-base project holds no tests).
> - Plan-text defects the implementers hit: PublicAPI lines for value-type parameters take no `!`; `<paramref>` to a
>   constructor parameter in a class summary is CS1734.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A public object deleted through `IStorageProvider` is also purged from the Cloudflare edge, and a refused purge reaches the caller as `CdnPurgeException`.

**Architecture:** `ICdnPurger`, `CdnPurgeException` and the `PurgingStorageProvider` decorator go in `Themia.Storage` (no new dependencies). The Cloudflare implementation, its options and DI go in a new package `Themia.Storage.Cloudflare`. The decorator is wired by hand in the slot factory of `AddThemiaSplitStorage` (public slot) or as the sole `IStorageProvider`; nothing changes for an app that does not call the new extension.

**Tech Stack:** .NET 8 + 10 (neutral package), xUnit, `IHttpClientFactory`, `System.Text.Json`, PublicApiAnalyzers.

**Spec:** `docs/superpowers/specs/2026-10-03-storage-cdn-purge-design.md` (coord #0153). Read §2, §3, §6 and §8 before starting; this plan implements them.

## Global Constraints

- `Themia.Storage.Cloudflare` targets `net8.0;net10.0` (neutral policy: the net8 leg is mandatory). Test projects target `net10.0`.
- `Directory.Build.props` has `TreatWarningsAsErrors=true` and `GenerateDocumentationFile=true`: every public member needs `///` docs (CS1591) and every public member must be in `PublicAPI.Unshipped.txt` (RS0016). Surface RS0016 with `dotnet build <project> --no-incremental`.
- `System.Text.Json` only; never `Newtonsoft.Json`. Log via `ILogger<T>` only.
- The Cloudflare API token never appears in a URL, message, exception, default header or log. It travels only in a per-request `Authorization: Bearer` header.
- `ICdnPurger.PurgeAsync(Uri, CancellationToken)` takes ONE URL. No batch API now (spec §3).
- A purge failure throws `CdnPurgeException`; a caller's own cancellation throws `OperationCanceledException`, never `CdnPurgeException`.
- No retry inside the purger or the decorator.
- Do not edit `Themia.Modules.Storage` behaviour (spec §8). Task 4 only adds a test that pins today's behaviour.
- Do not run all of `Themia.sln`; run the named test projects only (`dotnet test <project> --logger "console;verbosity=minimal"`). Quiet builds: `dotnet build <project> -v q --nologo`.
- Git: commit locally on the current branch; do NOT push or open a PR unless the user says so. Public repo: **no `git commit` Mon-Fri 09:00-18:00** (check `date '+%a %H'`); outside those hours commit freely. Commit messages: `<type>: <imperative subject>`, no `Co-Authored-By` and no "Generated with" lines.
- Match existing style: file-scoped namespaces, `///` summaries, primary constructors where the neighbours use them.

## Review Focus

The spec implies these failure modes; each has a test in the task that owns the code.

1. **A key with a space or non-ASCII character** (`photos/บ้าน 1.jpg`): the purge body must carry `Uri.AbsoluteUri` (percent-encoded), not `ToString()` (decoded), or Cloudflare purges a URL the edge never cached. Task 2.
2. **A public slot with no public container** (`GetPublicUrl` throws): the decorator must fail BEFORE deleting, not after. Task 1.
3. **A caller's token cancelled mid-purge**: must surface as `OperationCanceledException`, not be relabelled "deleted, cache stale". Task 2.
4. **A 200 with a non-Cloudflare body** (HTML from a proxy, empty body, JSON without `success`): success cannot be confirmed, so it throws. Task 2.
5. **`Enabled=false` with the decorator still wired and blank zone/token**: host starts, delete works, zero HTTP calls. Task 3.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/neutral/Themia.Storage/ICdnPurger.cs` (new) | The abstraction: purge one URL, throw `CdnPurgeException` on refusal. |
| `src/neutral/Themia.Storage/CdnPurgeException.cs` (new) | "The delete succeeded and the cache may still hold the object." |
| `src/neutral/Themia.Storage/PurgingStorageProvider.cs` (new) | Decorator: URL, delete, purge for `public/` keys; forwards everything else. |
| `src/neutral/Themia.Storage.Cloudflare/` (new package) | `CloudflarePurgeOptions`, `CloudflareCdnPurger`, internal `NoOpCdnPurger`, `StorageCloudflareServiceCollectionExtensions`. |
| `tests/Themia.Storage.Tests/PurgingStorageProviderTests.cs` (new) | Decorator tests over the existing `SpyStorageProvider`. |
| `tests/Themia.Storage.Cloudflare.Tests/` (new project) | Purger, DI, redaction and real-graph tests. |
| `tests/Themia.Modules.Storage.IntegrationTests/StorageConformanceTests.cs` (modify) | Pin the module's best-effort delete (spec §8). Needs Docker. |

---

### Task 1: Abstraction and decorator in `Themia.Storage`

**Files:**
- Create: `src/neutral/Themia.Storage/ICdnPurger.cs`, `src/neutral/Themia.Storage/CdnPurgeException.cs`, `src/neutral/Themia.Storage/PurgingStorageProvider.cs`
- Modify: `src/neutral/Themia.Storage/PublicAPI.Unshipped.txt`, `tests/Themia.Storage.Tests/SpyStorageProvider.cs` (add `ThrowOnDelete`)
- Test: `tests/Themia.Storage.Tests/PurgingStorageProviderTests.cs`

**Interfaces:**
- Consumes: `IStorageProvider`, `StorageKey.NormalizeAndValidate/IsPublic`, `SplitStorageProvider`, `AddThemiaSplitStorage(Func, Func)`, test double `SpyStorageProvider(string name, bool hasPublicContainer)` whose `GetPublicUrl("public/a.jpg")` returns `https://{name}.example.com/a.jpg` and whose `Calls` list records `Delete:{key}` / `PublicUrl:{key}`.
- Produces:
  - `public interface ICdnPurger { Task PurgeAsync(Uri url, CancellationToken cancellationToken = default); }`
  - `public sealed class CdnPurgeException : Exception` with `CdnPurgeException(Uri url, string message, int? httpStatus = null, Exception? innerException = null)`, `Uri Url`, `int? HttpStatus`.
  - `public sealed class PurgingStorageProvider : IStorageProvider` with `PurgingStorageProvider(IStorageProvider inner, ICdnPurger purger)`.

- [ ] **Step 1: Let the spy fail a delete**

In `tests/Themia.Storage.Tests/SpyStorageProvider.cs` add the property next to `ThrowOnDispose`:

```csharp
    public Exception? ThrowOnDelete { get; init; }
```

and change `DeleteAsync` to:

```csharp
    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Calls.Add($"Delete:{key}");
        if (ThrowOnDelete is not null)
        {
            throw ThrowOnDelete;
        }

        return Task.CompletedTask;
    }
```

- [ ] **Step 2: Write the failing tests**

Create `tests/Themia.Storage.Tests/PurgingStorageProviderTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Themia.Storage.Local;
using Xunit;

namespace Themia.Storage.Tests;

/// <summary>
/// The decorator that purges the CDN edge after a public delete (coord #0153): delete first, purge only
/// public keys, let a refused purge surface, and compose with the split router without owning its slot.
/// </summary>
public sealed class PurgingStorageProviderTests
{
    private readonly SpyStorageProvider inner = new("cdn", hasPublicContainer: true);
    private readonly SpyCdnPurger purger;

    public PurgingStorageProviderTests() => purger = new SpyCdnPurger(inner.Calls);

    private PurgingStorageProvider Create() => new(inner, purger);

    [Fact]
    public async Task A_public_delete_removes_the_object_and_only_then_purges_the_url_the_slot_serves()
    {
        await Create().DeleteAsync("public/a.jpg");

        Assert.Equal(new Uri("https://cdn.example.com/a.jpg"), Assert.Single(purger.Purged));
        Assert.Contains("Delete:public/a.jpg", purger.InnerCallsAtPurge);
    }

    [Fact]
    public async Task A_failed_delete_throws_the_inner_exception_and_never_purges()
    {
        var failing = new SpyStorageProvider("cdn", hasPublicContainer: true) { ThrowOnDelete = new IOException("disk gone") };
        var provider = new PurgingStorageProvider(failing, purger);

        await Assert.ThrowsAsync<IOException>(() => provider.DeleteAsync("public/a.jpg"));

        Assert.Empty(purger.Purged);
    }

    [Fact]
    public async Task A_public_key_whose_url_cannot_be_built_fails_before_anything_is_deleted()
    {
        var noContainer = new SpyStorageProvider("cdn", hasPublicContainer: false);
        var provider = new PurgingStorageProvider(noContainer, purger);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.DeleteAsync("public/a.jpg"));

        Assert.DoesNotContain("Delete:public/a.jpg", noContainer.Calls);
    }

    [Theory]
    [InlineData("a.pdf")]
    [InlineData("private/a.pdf")]
    [InlineData("Public/a.jpg")]
    [InlineData("_platform/x")]
    public async Task A_key_outside_public_is_deleted_and_never_purged(string key)
    {
        await Create().DeleteAsync(key);

        Assert.Contains($"Delete:{key}", inner.Calls);
        Assert.Empty(purger.Purged);
    }

    [Fact]
    public async Task A_backslash_key_is_normalised_before_it_is_classified()
    {
        await Create().DeleteAsync(@"public\a.jpg");

        Assert.Contains("Delete:public/a.jpg", inner.Calls);
        Assert.Single(purger.Purged);
    }

    [Theory]
    [InlineData("/public/a.jpg")]
    [InlineData("public/../a.jpg")]
    public async Task An_invalid_key_throws_and_nothing_is_touched(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Create().DeleteAsync(key));

        Assert.Empty(inner.Calls);
        Assert.Empty(purger.Purged);
    }

    [Fact]
    public async Task A_refused_purge_surfaces_after_the_object_is_already_deleted()
    {
        purger.Throws = new CdnPurgeException(new Uri("https://cdn.example.com/a.jpg"), "refused", httpStatus: 429);

        var thrown = await Assert.ThrowsAsync<CdnPurgeException>(() => Create().DeleteAsync("public/a.jpg"));

        Assert.Equal(429, thrown.HttpStatus);
        Assert.Contains("Delete:public/a.jpg", inner.Calls);
    }

    [Fact]
    public async Task Every_other_member_forwards_and_never_purges()
    {
        var provider = Create();
        var presign = new PresignedUrlRequest(PresignedUrlOperation.Get, TimeSpan.FromMinutes(1));

        await provider.PutAsync("public/a.jpg", new MemoryStream([1]), new StoragePutOptions("image/jpeg", Visibility: StorageVisibility.Public));
        await provider.GetAsync("public/a.jpg");
        await provider.ExistsAsync("public/a.jpg");
        await provider.StatAsync("public/a.jpg");
        await provider.GetPresignedUrlAsync("public/a.jpg", presign);
        var url = provider.GetPublicUrl("public/a.jpg");

        Assert.Equal(new Uri("https://cdn.example.com/a.jpg"), url);
        Assert.Equal(
            new[] { "Put:public/a.jpg", "Get:public/a.jpg", "Exists:public/a.jpg", "Stat:public/a.jpg", "Presign:public/a.jpg", "PublicUrl:public/a.jpg" },
            inner.Calls);
        Assert.Empty(purger.Purged);
    }

    [Fact]
    public async Task Wrapping_the_public_slot_of_the_router_purges_public_deletes_only()
    {
        var privateSlot = new SpyStorageProvider("private");
        var router = new SplitStorageProvider(new PurgingStorageProvider(inner, purger), privateSlot);

        await router.DeleteAsync("public/a.jpg");
        await router.DeleteAsync("docs/id.pdf");

        Assert.Single(purger.Purged);
        Assert.Contains("Delete:docs/id.pdf", privateSlot.Calls);
    }

    [Fact]
    public void The_container_disposes_a_wrapped_slot_exactly_once()
    {
        var slot = new SpyStorageProvider("cdn", hasPublicContainer: true);
        var services = new ServiceCollection();
        services.AddSingleton<ICdnPurger>(purger);
        services.AddSingleton(_ => slot);
        services.AddThemiaSplitStorage(
            sp => new PurgingStorageProvider(sp.GetRequiredService<SpyStorageProvider>(), sp.GetRequiredService<ICdnPurger>()),
            _ => new SpyStorageProvider("private"));

        using (var provider = services.BuildServiceProvider())
        {
            _ = provider.GetRequiredService<IStorageProvider>();
        }

        Assert.Equal(1, slot.DisposeCount);
    }

    [Fact]
    public async Task A_refused_purge_leaves_the_object_deleted_and_a_second_delete_completes_the_pair()
    {
        var root = Path.Combine(Path.GetTempPath(), "themia-purge-private", Guid.NewGuid().ToString("N"));
        var publicRoot = Path.Combine(Path.GetTempPath(), "themia-purge-public", Guid.NewGuid().ToString("N"));
        try
        {
            var local = new LocalStorageProvider(new LocalStorageOptions
            {
                RootPath = root,
                PublicRootPath = publicRoot,
                PublicBaseUrl = "https://cdn.example.com/media",
                SigningKey = "test-signing-key-at-least-32-characters-long",
            });
            await local.PutAsync("public/a.jpg", new MemoryStream([1]), new StoragePutOptions("image/jpeg", Visibility: StorageVisibility.Public));
            var flaky = new SpyCdnPurger([]) { Throws = new CdnPurgeException(new Uri("https://cdn.example.com/media/a.jpg"), "refused", 429) };
            var provider = new PurgingStorageProvider(local, flaky);

            await Assert.ThrowsAsync<CdnPurgeException>(() => provider.DeleteAsync("public/a.jpg"));
            Assert.False(await local.ExistsAsync("public/a.jpg"));

            flaky.Throws = null;
            await provider.DeleteAsync("public/a.jpg");

            Assert.Equal(2, flaky.Purged.Count);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(publicRoot)) Directory.Delete(publicRoot, recursive: true);
        }
    }
}

/// <summary>A purger that records what it was asked to purge and what the inner provider had seen by then.</summary>
internal sealed class SpyCdnPurger(List<string> innerCalls) : ICdnPurger
{
    public List<Uri> Purged { get; } = [];

    public List<string> InnerCallsAtPurge { get; } = [];

    public Exception? Throws { get; set; }

    public Task PurgeAsync(Uri url, CancellationToken cancellationToken = default)
    {
        InnerCallsAtPurge.AddRange(innerCalls);
        Purged.Add(url);
        return Throws is null ? Task.CompletedTask : Task.FromException(Throws);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail to compile**

Run: `dotnet test tests/Themia.Storage.Tests --logger "console;verbosity=minimal" 2>&1 | tail -15`
Expected: build FAIL, `ICdnPurger`, `CdnPurgeException` and `PurgingStorageProvider` do not exist.

- [ ] **Step 4: Write the abstraction, the exception and the decorator**

`src/neutral/Themia.Storage/ICdnPurger.cs`:

```csharp
namespace Themia.Storage;

/// <summary>
/// Clears a CDN's cached copy of one public object. <see cref="PurgingStorageProvider"/> calls it after the
/// object has been deleted from storage; an implementation is vendor-specific
/// (<c>Themia.Storage.Cloudflare</c> is the first).
/// </summary>
public interface ICdnPurger
{
    /// <summary>Purges the cached copy of <paramref name="url"/>, the public URL the CDN was asked for.</summary>
    /// <param name="url">The absolute public URL of the deleted object.</param>
    /// <param name="cancellationToken">Cancels the purge.</param>
    /// <exception cref="CdnPurgeException">The CDN refused the purge, or the purge could not be confirmed.</exception>
    Task PurgeAsync(Uri url, CancellationToken cancellationToken = default);
}
```

`src/neutral/Themia.Storage/CdnPurgeException.cs`:

```csharp
namespace Themia.Storage;

/// <summary>
/// The object was deleted from storage but the CDN edge may still serve it. It means exactly that: a failed
/// delete throws whatever the inner provider throws. Recover by repeating the delete and purge; delete is
/// idempotent.
/// </summary>
public sealed class CdnPurgeException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="url">The public URL that could not be purged.</param>
    /// <param name="message">What happened, including that the object is already deleted.</param>
    /// <param name="httpStatus">The HTTP status the CDN answered with, or <see langword="null"/> when there was no usable answer.</param>
    /// <param name="innerException">The transport failure, when there was one.</param>
    public CdnPurgeException(Uri url, string message, int? httpStatus = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Url = url;
        HttpStatus = httpStatus;
    }

    /// <summary>The public URL that could not be purged.</summary>
    public Uri Url { get; }

    /// <summary>The HTTP status the CDN answered with; <see langword="null"/> for a transport failure or no answer.</summary>
    public int? HttpStatus { get; }
}
```

`src/neutral/Themia.Storage/PurgingStorageProvider.cs`:

```csharp
namespace Themia.Storage;

/// <summary>
/// An <see cref="IStorageProvider"/> that, after deleting a <c>public/</c> object, purges the CDN's copy of
/// the URL <c>inner.GetPublicUrl(key)</c> (coord #0153). Wrap the public slot of a split provider, or the
/// single provider. Every other member forwards unchanged, and a non-public key is never purged.
/// This type never disposes <paramref name="inner"/>: whoever constructs it owns it.
/// </summary>
public sealed class PurgingStorageProvider : IStorageProvider
{
    private readonly IStorageProvider inner;
    private readonly ICdnPurger purger;

    /// <summary>Wraps <paramref name="inner"/>.</summary>
    /// <param name="inner">The provider that serves the public URLs.</param>
    /// <param name="purger">The CDN purger called after a public delete.</param>
    public PurgingStorageProvider(IStorageProvider inner, ICdnPurger purger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(purger);
        this.inner = inner;
        this.purger = purger;
    }

    /// <inheritdoc />
    public Task<StorageObjectInfo> PutAsync(string key, Stream content, StoragePutOptions options, CancellationToken cancellationToken = default) =>
        inner.PutAsync(key, content, options, cancellationToken);

    /// <inheritdoc />
    public Task<StorageReadResult?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        inner.GetAsync(key, cancellationToken);

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
        inner.ExistsAsync(key, cancellationToken);

    /// <inheritdoc />
    public Task<StorageObjectInfo?> StatAsync(string key, CancellationToken cancellationToken = default) =>
        inner.StatAsync(key, cancellationToken);

    /// <inheritdoc />
    public Task<Uri> GetPresignedUrlAsync(string key, PresignedUrlRequest request, CancellationToken cancellationToken = default) =>
        inner.GetPresignedUrlAsync(key, request, cancellationToken);

    /// <inheritdoc />
    public Uri GetPublicUrl(string key) => inner.GetPublicUrl(key);

    /// <summary>
    /// Deletes the object, then, for a <c>public/</c> key, purges its public URL. The URL is built first so a
    /// public slot with no public container fails before anything is deleted.
    /// </summary>
    /// <exception cref="CdnPurgeException">The object is deleted but the CDN refused the purge.</exception>
    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var normalized = StorageKey.NormalizeAndValidate(key);
        var publicUrl = StorageKey.IsPublic(normalized) ? inner.GetPublicUrl(normalized) : null;

        await inner.DeleteAsync(normalized, cancellationToken).ConfigureAwait(false);

        if (publicUrl is not null)
        {
            await purger.PurgeAsync(publicUrl, cancellationToken).ConfigureAwait(false);
        }
    }
}
```

- [ ] **Step 5: Add the PublicAPI entries**

Append to `src/neutral/Themia.Storage/PublicAPI.Unshipped.txt` (keep the existing first line `#nullable enable`):

```
Themia.Storage.ICdnPurger
Themia.Storage.ICdnPurger.PurgeAsync(System.Uri! url, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task!
Themia.Storage.CdnPurgeException
Themia.Storage.CdnPurgeException.CdnPurgeException(System.Uri! url, string! message, int? httpStatus = null, System.Exception? innerException = null) -> void
Themia.Storage.CdnPurgeException.HttpStatus.get -> int?
Themia.Storage.CdnPurgeException.Url.get -> System.Uri!
Themia.Storage.PurgingStorageProvider
Themia.Storage.PurgingStorageProvider.DeleteAsync(string! key, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task!
Themia.Storage.PurgingStorageProvider.ExistsAsync(string! key, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<bool>!
Themia.Storage.PurgingStorageProvider.GetAsync(string! key, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<Themia.Storage.StorageReadResult?>!
Themia.Storage.PurgingStorageProvider.GetPresignedUrlAsync(string! key, Themia.Storage.PresignedUrlRequest! request, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<System.Uri!>!
Themia.Storage.PurgingStorageProvider.GetPublicUrl(string! key) -> System.Uri!
Themia.Storage.PurgingStorageProvider.PurgingStorageProvider(Themia.Storage.IStorageProvider! inner, Themia.Storage.ICdnPurger! purger) -> void
Themia.Storage.PurgingStorageProvider.PutAsync(string! key, System.IO.Stream! content, Themia.Storage.StoragePutOptions! options, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<Themia.Storage.StorageObjectInfo!>!
Themia.Storage.PurgingStorageProvider.StatAsync(string! key, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<Themia.Storage.StorageObjectInfo?>!
```

Run: `dotnet build src/neutral/Themia.Storage -v q --nologo --no-incremental 2>&1 | tail -20`
Expected: 0 errors. If RS0016 reports a line that differs from the ones above (analyzer text is authoritative, for example a `StoragePutOptions!` nullability mark), replace the offending line with the analyzer's exact text and rebuild until clean.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Themia.Storage.Tests --logger "console;verbosity=minimal" 2>&1 | tail -15`
Expected: PASS (all existing storage tests plus the 15 new cases).

- [ ] **Step 7: Falsify the tests, then restore**

Temporarily apply each mutation to `PurgingStorageProvider.DeleteAsync`, run `dotnet test tests/Themia.Storage.Tests --filter PurgingStorageProviderTests`, confirm the named tests FAIL, then revert:
1. Move the `purger.PurgeAsync` call above `inner.DeleteAsync`: `A_public_delete_removes_the_object_and_only_then_purges...` must fail.
2. Build `publicUrl` after the delete (move the line below `inner.DeleteAsync`): `A_public_key_whose_url_cannot_be_built...` must fail.
3. Drop the `StorageKey.IsPublic` check (always purge): the `A_key_outside_public...` cases must fail.
4. Add `: IDisposable` with a `Dispose()` that disposes `inner`: `The_container_disposes_a_wrapped_slot_exactly_once` must fail (count 2).
5. Make `GetPublicUrl` throw `NotSupportedException`: `Wrapping_the_public_slot_of_the_router...` must fail (the router probe).

- [ ] **Step 8: Commit**

```bash
git add src/neutral/Themia.Storage tests/Themia.Storage.Tests
git commit -m "feat: add PurgingStorageProvider and ICdnPurger to Themia.Storage"
```

---

### Task 2: The Cloudflare purger

**Files:**
- Create: `src/neutral/Themia.Storage.Cloudflare/Themia.Storage.Cloudflare.csproj`, `CloudflarePurgeOptions.cs`, `CloudflareCdnPurger.cs`, `PublicAPI.Shipped.txt`, `PublicAPI.Unshipped.txt`
- Create: `tests/Themia.Storage.Cloudflare.Tests/Themia.Storage.Cloudflare.Tests.csproj`, `CloudflareCdnPurgerTests.cs`, `TestDoubles.cs`
- Modify: `Themia.sln` (via `dotnet sln add`)

**Interfaces:**
- Consumes: `ICdnPurger`, `CdnPurgeException(Uri, string, int?, Exception?)` from Task 1.
- Produces:
  - `public sealed class CloudflarePurgeOptions { bool Enabled; string ZoneId; string ApiToken; }` (all settable; defaults `false`, `""`, `""`).
  - `public sealed class CloudflareCdnPurger : ICdnPurger` with `public const string HttpClientName = "Themia.Storage.Cloudflare"`, ctor `(IHttpClientFactory httpClientFactory, IOptions<CloudflarePurgeOptions> options)`, and `internal static readonly Uri ApiBaseAddress = new("https://api.cloudflare.com/client/v4/")`.

- [ ] **Step 1: Create the two projects and add them to the solution**

`src/neutral/Themia.Storage.Cloudflare/Themia.Storage.Cloudflare.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- Neutral cross-framework package: MUST include net8.0 (cross-framework reuse). -->
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <PackageId>Themia.Storage.Cloudflare</PackageId>
    <Description>Cloudflare cache-purge implementation of Themia.Storage's ICdnPurger: after a public object is deleted, purge its URL from the edge. Uses its own Zone/Cache Purge token, which is never logged.</Description>
    <PackageTags>themia;storage;cloudflare;cdn;cache-purge</PackageTags>
    <!-- Version is inherited from Directory.Build.props (shared). Do not set it here. -->
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Themia.Storage/Themia.Storage.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Http" />
    <PackageReference Include="Microsoft.Extensions.Options" />
  </ItemGroup>
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
    <InternalsVisibleTo Include="Themia.Storage.Cloudflare.Tests" />
  </ItemGroup>
</Project>
```

`src/neutral/Themia.Storage.Cloudflare/PublicAPI.Shipped.txt` contains exactly one line: `#nullable enable`. `PublicAPI.Unshipped.txt` the same single line for now.

`tests/Themia.Storage.Cloudflare.Tests/Themia.Storage.Cloudflare.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.Extensions.Http" />
    <PackageReference Include="Microsoft.Extensions.Logging" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/neutral/Themia.Storage.Cloudflare/Themia.Storage.Cloudflare.csproj" />
  </ItemGroup>
</Project>
```

Run: `dotnet sln Themia.sln add src/neutral/Themia.Storage.Cloudflare/Themia.Storage.Cloudflare.csproj tests/Themia.Storage.Cloudflare.Tests/Themia.Storage.Cloudflare.Tests.csproj 2>&1 | tail -3`
Expected: both projects added. If the solution groups projects into solution folders (check how `Themia.Storage.S3` appears in `Themia.sln` with `grep -n 'Storage.S3' Themia.sln`), nest the new ones the same way by copying the `NestedProjects` lines for `Themia.Storage.S3` and its test project.

- [ ] **Step 2: Write the test doubles**

`tests/Themia.Storage.Cloudflare.Tests/TestDoubles.cs`:

```csharp
using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;

namespace Themia.Storage.Cloudflare.Tests;

/// <summary>What a stub handler saw, copied at send time (the request content is disposed afterwards).</summary>
internal sealed record SeenRequest(HttpMethod Method, Uri Uri, string? Authorization, string Body);

/// <summary>Answers every request with a fixed status and body, and records each request.</summary>
internal sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    public List<SeenRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var content = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new SeenRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), content));
        return new HttpResponseMessage(status) { Content = new StringContent(body) };
    }
}

/// <summary>Fails the way a dead network does: no response at all.</summary>
internal sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw exception;
    }
}

/// <summary>Captures every message logged through it, across every category, including the framework's own HttpClient loggers.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentBag<string> messages = new();

    public IReadOnlyCollection<string> AllMessages => messages.ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, messages);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string categoryName, ConcurrentBag<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            messages.Add($"[{categoryName}] {formatter(state, exception)}");
    }
}
```

- [ ] **Step 3: Write the failing purger tests**

`tests/Themia.Storage.Cloudflare.Tests/CloudflareCdnPurgerTests.cs`:

```csharp
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Themia.Storage.Cloudflare.Tests;

/// <summary>
/// The Cloudflare purge call (coord #0153): request shape, and every way a purge can fail, checked against a
/// stub handler. Response shapes follow Cloudflare's published purge documentation.
/// </summary>
public sealed class CloudflareCdnPurgerTests
{
    private const string Token = "cf-secret-token-0123456789";
    private const string SuccessBody = """{"errors":[],"messages":[],"result":{"id":"023e105f4ecef8ad9ca31a8372d0c353"},"success":true}""";
    private const string RateLimitedBody = """{"errors":[{"code":1134,"message":"Unable to purge, rate limit reached. Please wait and consider throttling your request speed"}],"messages":[],"result":null,"success":false}""";
    private const string BadRequestBody = """{"errors":[{"code":1092,"message":"Request cannot contain a bad thing"}],"messages":[],"result":null,"success":false}""";

    private static readonly Uri Photo = new("https://img.example.com/photos/บ้าน 1.jpg");

    private static CloudflareCdnPurger PurgerFor(HttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(CloudflareCdnPurger.HttpClientName, client => client.BaseAddress = CloudflareCdnPurger.ApiBaseAddress)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddSingleton<IOptions<CloudflarePurgeOptions>>(
            Options.Create(new CloudflarePurgeOptions { Enabled = true, ZoneId = "zone-1", ApiToken = Token }));
        services.AddSingleton<CloudflareCdnPurger>();
        return services.BuildServiceProvider().GetRequiredService<CloudflareCdnPurger>();
    }

    [Fact]
    public async Task Posts_one_file_to_the_zone_purge_endpoint_with_the_bearer_token()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, SuccessBody);

        await PurgerFor(handler).PurgeAsync(Photo);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.cloudflare.com/client/v4/zones/zone-1/purge_cache", request.Uri.AbsoluteUri);
        Assert.Equal($"Bearer {Token}", request.Authorization);
        Assert.Equal($$"""{"files":["{{Photo.AbsoluteUri}}"]}""", request.Body);
    }

    [Fact]
    public async Task Sends_the_percent_encoded_url_the_edge_was_asked_for_not_the_decoded_one()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, SuccessBody);

        await PurgerFor(handler).PurgeAsync(Photo);

        var body = Assert.Single(handler.Requests).Body;
        Assert.Contains("%20", body);
        Assert.DoesNotContain("บ้าน", body);
    }

    [Theory]
    [InlineData(429, RateLimitedBody)]
    [InlineData(500, "")]
    [InlineData(403, """{"errors":[{"code":10000,"message":"Authentication error"}],"success":false}""")]
    [InlineData(200, BadRequestBody)]
    [InlineData(200, "")]
    [InlineData(200, "<html>proxy login</html>")]
    [InlineData(200, """{"errors":[],"messages":[]}""")]
    public async Task A_refused_or_unconfirmed_purge_throws_CdnPurgeException_with_the_status(int status, string body)
    {
        var handler = new RecordingHandler((HttpStatusCode)status, body);

        var thrown = await Assert.ThrowsAsync<CdnPurgeException>(() => PurgerFor(handler).PurgeAsync(Photo));

        Assert.Equal(status, thrown.HttpStatus);
        Assert.Equal(Photo, thrown.Url);
        Assert.Contains("already deleted", thrown.Message);
        Assert.DoesNotContain(Token, thrown.ToString());
    }

    [Fact]
    public async Task The_message_carries_cloudflares_first_error_code()
    {
        var handler = new RecordingHandler(HttpStatusCode.TooManyRequests, RateLimitedBody);

        var thrown = await Assert.ThrowsAsync<CdnPurgeException>(() => PurgerFor(handler).PurgeAsync(Photo));

        Assert.Contains("1134", thrown.Message);
    }

    [Fact]
    public async Task A_huge_error_message_is_capped_and_the_body_is_not_echoed()
    {
        var huge = new string('x', 5000);
        var handler = new RecordingHandler(HttpStatusCode.BadRequest, $$"""{"errors":[{"code":1,"message":"{{huge}}"}],"success":false}""");

        var thrown = await Assert.ThrowsAsync<CdnPurgeException>(() => PurgerFor(handler).PurgeAsync(Photo));

        Assert.True(thrown.Message.Length < 700, $"message was {thrown.Message.Length} characters");
    }

    [Fact]
    public async Task A_transport_failure_throws_CdnPurgeException_without_a_status()
    {
        var handler = new ThrowingHandler(new HttpRequestException("connection refused"));

        var thrown = await Assert.ThrowsAsync<CdnPurgeException>(() => PurgerFor(handler).PurgeAsync(Photo));

        Assert.Null(thrown.HttpStatus);
        Assert.IsType<HttpRequestException>(thrown.InnerException);
    }

    [Fact]
    public async Task The_http_clients_own_timeout_throws_CdnPurgeException()
    {
        var handler = new ThrowingHandler(new TaskCanceledException("timed out", new TimeoutException()));

        var thrown = await Assert.ThrowsAsync<CdnPurgeException>(() => PurgerFor(handler).PurgeAsync(Photo));

        Assert.Null(thrown.HttpStatus);
    }

    [Fact]
    public async Task A_callers_own_cancellation_is_not_relabelled_as_a_purge_failure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var handler = new ThrowingHandler(new OperationCanceledException(cts.Token));

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PurgerFor(handler).PurgeAsync(Photo, cts.Token));

        Assert.IsNotType<CdnPurgeException>(thrown);
    }
}
```

- [ ] **Step 4: Run to verify the build fails**

Run: `dotnet test tests/Themia.Storage.Cloudflare.Tests --logger "console;verbosity=minimal" 2>&1 | tail -8`
Expected: build FAIL, `CloudflareCdnPurger` and `CloudflarePurgeOptions` do not exist.

- [ ] **Step 5: Implement options and purger**

`src/neutral/Themia.Storage.Cloudflare/CloudflarePurgeOptions.cs`:

```csharp
namespace Themia.Storage.Cloudflare;

/// <summary>Configuration for the Cloudflare cache purge (coord #0153). Off unless <see cref="Enabled"/> is set.</summary>
public sealed class CloudflarePurgeOptions
{
    /// <summary>
    /// Whether public deletes are purged from the edge. <see langword="false"/> registers a no-op purger, so the
    /// decorator can stay wired in an environment with no CDN (for example Local development).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>The Cloudflare zone id that serves the public base URL's host. Required when <see cref="Enabled"/>.</summary>
    public string ZoneId { get; set; } = string.Empty;

    /// <summary>
    /// A Cloudflare API token scoped to Zone, Cache Purge on that one zone. It is a separate credential from the
    /// storage (R2) keys and must not be the same token. Sent only as a Bearer header; never logged. Required when <see cref="Enabled"/>.
    /// </summary>
    public string ApiToken { get; set; } = string.Empty;
}
```

`src/neutral/Themia.Storage.Cloudflare/CloudflareCdnPurger.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Themia.Storage.Cloudflare;

/// <summary>
/// Purges one URL from Cloudflare's cache: <c>POST /zones/{zone}/purge_cache</c> with <c>{"files":[url]}</c>.
/// Anything short of a 2xx answer with <c>success:true</c> throws <see cref="CdnPurgeException"/>; a 200 means
/// Cloudflare accepted the request, not that removal was confirmed.
/// </summary>
public sealed class CloudflareCdnPurger(IHttpClientFactory httpClientFactory, IOptions<CloudflarePurgeOptions> options) : ICdnPurger
{
    /// <summary>The named <see cref="HttpClient"/> this purger resolves via <see cref="IHttpClientFactory"/>.</summary>
    public const string HttpClientName = "Themia.Storage.Cloudflare";

    internal static readonly Uri ApiBaseAddress = new("https://api.cloudflare.com/client/v4/");

    private const int MaxErrorMessageLength = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <inheritdoc />
    public async Task PurgeAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        var settings = options.Value;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"zones/{Uri.EscapeDataString(settings.ZoneId)}/purge_cache")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new PurgeRequest([url.AbsoluteUri]), JsonOptions), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiToken);

        try
        {
            using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var failure = DescribeFailure(response.StatusCode, body);
            if (failure is not null)
            {
                throw Refused(url, failure, (int)response.StatusCode, innerException: null);
            }
        }
        catch (HttpRequestException ex)
        {
            throw Refused(url, "Cloudflare could not be reached.", httpStatus: null, ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller's token is still clear, so this is HttpClient's own timeout firing.
            throw Refused(url, "The Cloudflare purge timed out.", httpStatus: null, ex);
        }
    }

    private static CdnPurgeException Refused(Uri url, string failure, int? httpStatus, Exception? innerException) =>
        new(
            url,
            $"{failure} The object is already deleted from storage; the CDN may still serve {url.AbsoluteUri}. Repeat the delete and purge.",
            httpStatus,
            innerException);

    private static string? DescribeFailure(HttpStatusCode status, string body)
    {
        var envelope = ParseEnvelope(body);
        var code = (int)status;

        if (code is >= 200 and < 300)
        {
            if (envelope?.Success == true)
            {
                return null;
            }

            return envelope is null
                ? $"Cloudflare answered HTTP {code} with a body that is not its purge response, so the purge cannot be confirmed."
                : $"Cloudflare answered HTTP {code} without success:true{FirstError(envelope)}.";
        }

        return $"Cloudflare refused the purge with HTTP {code}{FirstError(envelope)}.";
    }

    private static string FirstError(PurgeResponse? envelope)
    {
        var error = envelope?.Errors?.FirstOrDefault();
        if (error is null)
        {
            return string.Empty;
        }

        var message = error.Message ?? string.Empty;
        if (message.Length > MaxErrorMessageLength)
        {
            message = message[..MaxErrorMessageLength];
        }

        return $": code {error.Code}, \"{message}\"";
    }

    private static PurgeResponse? ParseEnvelope(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<PurgeResponse>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record PurgeRequest([property: JsonPropertyName("files")] string[] Files);

    private sealed record PurgeResponse(bool? Success, List<PurgeError>? Errors);

    private sealed record PurgeError(int? Code, string? Message);
}
```

PublicAPI: write into `src/neutral/Themia.Storage.Cloudflare/PublicAPI.Unshipped.txt` (after `#nullable enable`):

```
const Themia.Storage.Cloudflare.CloudflareCdnPurger.HttpClientName = "Themia.Storage.Cloudflare" -> string!
Themia.Storage.Cloudflare.CloudflareCdnPurger
Themia.Storage.Cloudflare.CloudflareCdnPurger.CloudflareCdnPurger(System.Net.Http.IHttpClientFactory! httpClientFactory, Microsoft.Extensions.Options.IOptions<Themia.Storage.Cloudflare.CloudflarePurgeOptions!>! options) -> void
Themia.Storage.Cloudflare.CloudflareCdnPurger.PurgeAsync(System.Uri! url, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task!
Themia.Storage.Cloudflare.CloudflarePurgeOptions
Themia.Storage.Cloudflare.CloudflarePurgeOptions.ApiToken.get -> string!
Themia.Storage.Cloudflare.CloudflarePurgeOptions.ApiToken.set -> void
Themia.Storage.Cloudflare.CloudflarePurgeOptions.CloudflarePurgeOptions() -> void
Themia.Storage.Cloudflare.CloudflarePurgeOptions.Enabled.get -> bool
Themia.Storage.Cloudflare.CloudflarePurgeOptions.Enabled.set -> void
Themia.Storage.Cloudflare.CloudflarePurgeOptions.ZoneId.get -> string!
Themia.Storage.Cloudflare.CloudflarePurgeOptions.ZoneId.set -> void
```

Run: `dotnet build src/neutral/Themia.Storage.Cloudflare -v q --nologo --no-incremental 2>&1 | tail -20`
Expected: 0 errors on both net8.0 and net10.0. Fix any RS0016 by copying the analyzer's exact line. `JsonSerializer.Deserialize<PurgeResponse>` on a positional record is supported on net8.0+.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Themia.Storage.Cloudflare.Tests --logger "console;verbosity=minimal" 2>&1 | tail -15`
Expected: PASS.

- [ ] **Step 7: Falsify the tests, then restore**

Apply each mutation to `CloudflareCdnPurger`, run the test project, confirm the named test FAILS, revert:
1. Serialize `url.ToString()` instead of `url.AbsoluteUri`: `Sends_the_percent_encoded_url...` must fail (`ToString()` decodes).
2. Treat any 2xx as success (`if (code is >= 200 and < 300) return null;`): the four `200` rows of `A_refused_or_unconfirmed_purge...` must fail.
3. Delete the `when (!cancellationToken.IsCancellationRequested)` filter: `A_callers_own_cancellation...` must fail.
4. Put `body` into the exception message: `A_huge_error_message_is_capped...` must fail.

- [ ] **Step 8: Commit**

```bash
git add src/neutral/Themia.Storage.Cloudflare tests/Themia.Storage.Cloudflare.Tests Themia.sln
git commit -m "feat: add Themia.Storage.Cloudflare with the Cloudflare cache purger"
```

---

### Task 3: DI extension, startup validation, token redaction, real graph

**Files:**
- Create: `src/neutral/Themia.Storage.Cloudflare/StorageCloudflareServiceCollectionExtensions.cs`, `src/neutral/Themia.Storage.Cloudflare/NoOpCdnPurger.cs`
- Modify: `src/neutral/Themia.Storage.Cloudflare/PublicAPI.Unshipped.txt`
- Test: `tests/Themia.Storage.Cloudflare.Tests/StorageCloudflareRegistrationTests.cs`

**Interfaces:**
- Consumes: `CloudflarePurgeOptions`, `CloudflareCdnPurger` (+ `HttpClientName`), `ICdnPurger`, `PurgingStorageProvider`, `AddThemiaSplitStorage(Func, Func)`, test doubles from Task 2.
- Produces: `public static IServiceCollection AddThemiaStorageCloudflarePurge(this IServiceCollection services, Action<CloudflarePurgeOptions> configure)` in namespace `Themia.Storage.Cloudflare`; registers `ICdnPurger` as a singleton that is `CloudflareCdnPurger` when `Enabled`, else an internal no-op; named client with base address, 15 s timeout, `Authorization` redacted in logs; options validated on start.

- [ ] **Step 1: Write the failing tests**

`tests/Themia.Storage.Cloudflare.Tests/StorageCloudflareRegistrationTests.cs`:

```csharp
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Themia.Storage.Local;
using Xunit;

namespace Themia.Storage.Cloudflare.Tests;

/// <summary>Wiring the purge (coord #0153): startup validation, inertness when disabled, the token never logged, and the real graph.</summary>
public sealed class StorageCloudflareRegistrationTests : IDisposable
{
    private const string Token = "cf-secret-token-0123456789";
    private const string SuccessBody = """{"errors":[],"messages":[],"result":{"id":"x"},"success":true}""";
    private static readonly Uri Photo = new("https://img.example.com/photos/a.jpg");

    private readonly string root = Path.Combine(Path.GetTempPath(), "themia-cf-private", Guid.NewGuid().ToString("N"));
    private readonly string publicRoot = Path.Combine(Path.GetTempPath(), "themia-cf-public", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        if (Directory.Exists(publicRoot)) Directory.Delete(publicRoot, recursive: true);
    }

    private static ServiceProvider Build(Action<CloudflarePurgeOptions> configure, HttpMessageHandler? handler = null, Action<IServiceCollection>? more = null)
    {
        var services = new ServiceCollection();
        services.AddThemiaStorageCloudflarePurge(configure);
        if (handler is not null)
        {
            services.AddHttpClient(CloudflareCdnPurger.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        }

        more?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static void Enabled(CloudflarePurgeOptions o)
    {
        o.Enabled = true;
        o.ZoneId = "zone-1";
        o.ApiToken = Token;
    }

    [Fact]
    public void Enabled_with_a_blank_zone_fails_host_start()
    {
        using var provider = Build(o => { Enabled(o); o.ZoneId = " "; });

        var thrown = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("ZoneId", thrown.Message);
    }

    [Fact]
    public void Enabled_with_a_blank_token_fails_host_start_without_echoing_a_token()
    {
        using var provider = Build(o => { Enabled(o); o.ApiToken = string.Empty; });

        var thrown = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("ApiToken", thrown.Message);
    }

    [Fact]
    public void Disabled_with_blank_settings_starts_and_so_do_the_bare_defaults()
    {
        using var disabled = Build(o => { o.Enabled = false; o.ZoneId = string.Empty; o.ApiToken = string.Empty; });
        using var defaults = Build(_ => { });

        disabled.GetRequiredService<IStartupValidator>().Validate();
        defaults.GetRequiredService<IStartupValidator>().Validate();
    }

    [Fact]
    public async Task Disabled_makes_no_http_call_and_does_not_throw()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, SuccessBody);
        using var provider = Build(o => o.Enabled = false, handler);

        await provider.GetRequiredService<ICdnPurger>().PurgeAsync(Photo);

        Assert.Empty(handler.Requests);
        Assert.IsNotType<CloudflareCdnPurger>(provider.GetRequiredService<ICdnPurger>());
    }

    [Fact]
    public void Enabled_resolves_the_cloudflare_purger()
    {
        using var provider = Build(Enabled);

        Assert.IsType<CloudflareCdnPurger>(provider.GetRequiredService<ICdnPurger>());
    }

    [Fact]
    public void The_named_client_has_the_api_base_address_and_a_fifteen_second_timeout()
    {
        using var provider = Build(Enabled);

        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(CloudflareCdnPurger.HttpClientName);

        Assert.Equal(CloudflareCdnPurger.ApiBaseAddress, client.BaseAddress);
        Assert.Equal(TimeSpan.FromSeconds(15), client.Timeout);
    }

    [Fact]
    public async Task The_token_never_reaches_a_trace_level_log()
    {
        var capture = new CapturingLoggerProvider();
        using var provider = Build(Enabled, new RecordingHandler(HttpStatusCode.OK, SuccessBody),
            services => services.AddLogging(b => { b.AddProvider(capture); b.SetMinimumLevel(LogLevel.Trace); }));

        await provider.GetRequiredService<ICdnPurger>().PurgeAsync(Photo);

        Assert.NotEmpty(capture.AllMessages);
        Assert.DoesNotContain(capture.AllMessages, m => m.Contains(Token));
    }

    [Fact]
    public async Task Control_the_same_capture_does_see_the_token_once_redaction_is_overridden()
    {
        // If this fails, the log never carries request headers at all, which makes the test above vacuous:
        // rethink how it observes the header before trusting it.
        var capture = new CapturingLoggerProvider();
        using var provider = Build(Enabled, new RecordingHandler(HttpStatusCode.OK, SuccessBody), services =>
        {
            services.AddLogging(b => { b.AddProvider(capture); b.SetMinimumLevel(LogLevel.Trace); });
            services.Configure<HttpClientFactoryOptions>(CloudflareCdnPurger.HttpClientName, o => o.ShouldRedactHeaderValue = _ => false);
        });

        await provider.GetRequiredService<ICdnPurger>().PurgeAsync(Photo);

        Assert.Contains(capture.AllMessages, m => m.Contains(Token));
    }

    [Fact]
    public async Task The_default_graph_purges_a_public_delete_and_leaves_a_private_delete_alone()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, SuccessBody);
        using var provider = Build(Enabled, handler, services =>
        {
            services.AddSingleton(_ => new LocalStorageProvider(new LocalStorageOptions
            {
                RootPath = Path.Combine(root, "unused-public-private-root"),
                PublicRootPath = publicRoot,
                PublicBaseUrl = "https://cdn.example.com/media",
                SigningKey = "test-signing-key-at-least-32-characters-long",
            }));
            services.AddThemiaSplitStorage(
                sp => new PurgingStorageProvider(sp.GetRequiredService<LocalStorageProvider>(), sp.GetRequiredService<ICdnPurger>()),
                _ => new LocalStorageProvider(new LocalStorageOptions { RootPath = root, SigningKey = "test-signing-key-at-least-32-characters-long" }));
        });
        var storage = provider.GetRequiredService<IStorageProvider>();
        await storage.PutAsync("public/a.jpg", new MemoryStream([1]), new StoragePutOptions("image/jpeg", Visibility: StorageVisibility.Public));
        await storage.PutAsync("docs/id.pdf", new MemoryStream([1]), new StoragePutOptions("application/pdf"));

        await storage.DeleteAsync("public/a.jpg");
        await storage.DeleteAsync("docs/id.pdf");

        var request = Assert.Single(handler.Requests);
        Assert.Contains("https://cdn.example.com/media/a.jpg", request.Body);
        Assert.False(await storage.ExistsAsync("public/a.jpg"));
        Assert.False(await storage.ExistsAsync("docs/id.pdf"));
    }

    [Fact]
    public async Task A_decorator_as_the_sole_provider_purges_through_the_same_registration()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, SuccessBody);
        using var provider = Build(Enabled, handler, services =>
        {
            services.AddSingleton(_ => new LocalStorageProvider(new LocalStorageOptions
            {
                RootPath = root,
                PublicRootPath = publicRoot,
                PublicBaseUrl = "https://cdn.example.com/media",
                SigningKey = "test-signing-key-at-least-32-characters-long",
            }));
            services.AddSingleton<IStorageProvider>(sp =>
                new PurgingStorageProvider(sp.GetRequiredService<LocalStorageProvider>(), sp.GetRequiredService<ICdnPurger>()));
        });

        await provider.GetRequiredService<IStorageProvider>().DeleteAsync("public/a.jpg");

        Assert.Single(handler.Requests);
    }
}
```

- [ ] **Step 2: Run to verify the build fails**

Run: `dotnet test tests/Themia.Storage.Cloudflare.Tests --logger "console;verbosity=minimal" 2>&1 | tail -8`
Expected: build FAIL, `AddThemiaStorageCloudflarePurge` does not exist.

- [ ] **Step 3: Implement the extension and the no-op**

`src/neutral/Themia.Storage.Cloudflare/NoOpCdnPurger.cs`:

```csharp
namespace Themia.Storage.Cloudflare;

/// <summary>The purger registered when <see cref="CloudflarePurgeOptions.Enabled"/> is off: purges nothing and makes no call.</summary>
internal sealed class NoOpCdnPurger : ICdnPurger
{
    public static readonly NoOpCdnPurger Instance = new();

    public Task PurgeAsync(Uri url, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
```

`src/neutral/Themia.Storage.Cloudflare/StorageCloudflareServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Themia.Storage.Cloudflare;

/// <summary>Registers the Cloudflare cache purge (coord #0153).</summary>
public static class StorageCloudflareServiceCollectionExtensions
{
    // Chosen, not a Cloudflare figure: a purge sits inside a delete, and HttpClient's 100 s default would stall a queue worker.
    private static readonly TimeSpan PurgeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Registers <see cref="ICdnPurger"/> backed by Cloudflare, off unless <see cref="CloudflarePurgeOptions.Enabled"/>
    /// is set. With it off the registered purger does nothing, so <see cref="PurgingStorageProvider"/> can stay wired
    /// in every environment. With it on, a blank <see cref="CloudflarePurgeOptions.ZoneId"/> or
    /// <see cref="CloudflarePurgeOptions.ApiToken"/> fails host start.
    /// Wiring the decorator is the app's job; see the package README.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Sets the options; bind them from configuration in the app.</param>
    public static IServiceCollection AddThemiaStorageCloudflarePurge(
        this IServiceCollection services, Action<CloudflarePurgeOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<CloudflarePurgeOptions>()
            .Configure(configure)
            .Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.ZoneId), "CloudflarePurgeOptions.ZoneId must be set when Enabled is true.")
            .Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.ApiToken), "CloudflarePurgeOptions.ApiToken must be set when Enabled is true.")
            .ValidateOnStart();

        // The token goes in a per-request Authorization header, never in a URL. Redact that header in the
        // framework's request logging explicitly rather than relying on a default.
        services.AddHttpClient(CloudflareCdnPurger.HttpClientName, client =>
            {
                client.BaseAddress = CloudflareCdnPurger.ApiBaseAddress;
                client.Timeout = PurgeTimeout;
            })
            .RedactLoggedHeaders(["Authorization"]);

        services.TryAddSingleton<ICdnPurger>(sp =>
            sp.GetRequiredService<IOptions<CloudflarePurgeOptions>>().Value.Enabled
                ? new CloudflareCdnPurger(sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<IOptions<CloudflarePurgeOptions>>())
                : NoOpCdnPurger.Instance);
        return services;
    }
}
```

Append to `src/neutral/Themia.Storage.Cloudflare/PublicAPI.Unshipped.txt`:

```
Themia.Storage.Cloudflare.StorageCloudflareServiceCollectionExtensions
static Themia.Storage.Cloudflare.StorageCloudflareServiceCollectionExtensions.AddThemiaStorageCloudflarePurge(this Microsoft.Extensions.DependencyInjection.IServiceCollection! services, System.Action<Themia.Storage.Cloudflare.CloudflarePurgeOptions!>! configure) -> Microsoft.Extensions.DependencyInjection.IServiceCollection!
```

Run: `dotnet build src/neutral/Themia.Storage.Cloudflare -v q --nologo --no-incremental 2>&1 | tail -20`
Expected: 0 errors on both TFMs (fix RS0016 from the analyzer's text if needed).

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Themia.Storage.Cloudflare.Tests --logger "console;verbosity=minimal" 2>&1 | tail -15`
Expected: PASS. If `Control_the_same_capture_does_see_the_token_once_redaction_is_overridden` FAILS, stop: request headers are not in the captured log, so `The_token_never_reaches_a_trace_level_log` proves nothing. Rework the observation (for example read the `LoggingHttpMessageHandler` output under a category filter) before continuing.

- [ ] **Step 5: Falsify the tests, then restore**

1. Remove `.ValidateOnStart()`: the two startup tests must fail (`IStartupValidator` is no longer registered).
2. Remove `.RedactLoggedHeaders(["Authorization"])` (leave the control test alone): if `The_token_never_reaches_a_trace_level_log` still passes, the framework default already redacts; say so in the commit body and keep the explicit call anyway. If it fails, the explicit call is what protects the token.
3. Make the registration unconditional (`new CloudflareCdnPurger(...)` always): `Disabled_makes_no_http_call...` must fail.
4. Change `Timeout` to 100 seconds: `The_named_client_has_...` must fail.

- [ ] **Step 6: Commit**

```bash
git add src/neutral/Themia.Storage.Cloudflare tests/Themia.Storage.Cloudflare.Tests
git commit -m "feat: register the Cloudflare purge with startup validation and a redacted token"
```

---

### Task 4: Pin the module's best-effort delete (spec §8)

**Files:**
- Modify: `tests/Themia.Modules.Storage.IntegrationTests/StorageConformanceTests.cs`

**Interfaces:**
- Consumes: the file's existing `NewScope(TenantId?, ..., providerOverride: Func<IStorageProvider, IStorageProvider>)`, `Bytes(string)`, `ResetAsync()`, `Scope.Storage` (`ITenantStorage`), and `CdnPurgeException` from Task 1.
- Produces: one new conformance test run for every engine; no production change.

This needs Docker (Testcontainers) for the engine fixtures. If Docker is unavailable locally, write and commit the test, and say so in the hand-off; CI (`integration.yml`) runs it.

- [ ] **Step 1: Write the test and a wrapper that refuses the purge**

In `tests/Themia.Modules.Storage.IntegrationTests/StorageConformanceTests.cs`, after the `ThrowingStorageProvider` class, add:

```csharp
/// <summary>Deletes for real, then reports a refused CDN purge, as a <c>PurgingStorageProvider</c> under the module would.</summary>
file sealed class PurgeRefusingStorageProvider(IStorageProvider inner) : IStorageProvider
{
    public Task<StorageObjectInfo> PutAsync(string key, Stream content, StoragePutOptions options, CancellationToken cancellationToken = default) =>
        inner.PutAsync(key, content, options, cancellationToken);

    public Task<StorageReadResult?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        inner.GetAsync(key, cancellationToken);

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
        inner.ExistsAsync(key, cancellationToken);

    public Task<StorageObjectInfo?> StatAsync(string key, CancellationToken cancellationToken = default) =>
        inner.StatAsync(key, cancellationToken);

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        await inner.DeleteAsync(key, cancellationToken);
        throw new CdnPurgeException(new Uri("https://cdn.example.com/x"), "Simulated refused purge.", httpStatus: 429);
    }

    public Task<Uri> GetPresignedUrlAsync(string key, PresignedUrlRequest request, CancellationToken cancellationToken = default) =>
        inner.GetPresignedUrlAsync(key, request, cancellationToken);

    public Uri GetPublicUrl(string key) => inner.GetPublicUrl(key);
}
```

Add the test inside `StorageConformanceTests`, directly after `Put_get_delete_round_trip`:

```csharp
    [Fact]
    public async Task Delete_completes_when_the_provider_reports_a_refused_purge()
    {
        // Characterisation of TenantStorage's documented best-effort blob delete (coord #0153, spec section 8):
        // the logical delete is committed first, then a provider failure is only logged. A CdnPurgeException from
        // a decorated provider is therefore swallowed under this module. Changing that is a deliberate decision.
        await ResetAsync();
        await using var s = NewScope(new TenantId("acme"), providerOverride: inner => new PurgeRefusingStorageProvider(inner));
        await s.Storage.PutAsync("docs/a.txt", Bytes("hello"), new StoragePutOptions("text/plain"));

        await s.Storage.DeleteAsync("docs/a.txt");

        Assert.False(await s.Storage.ExistsAsync("docs/a.txt"));
    }
```

If the file lacks `using Themia.Storage;` for `CdnPurgeException`, it already has it (the other doubles use `IStorageProvider`); no new using is expected.

- [ ] **Step 2: Run it**

Run: `dotnet test tests/Themia.Modules.Storage.IntegrationTests --filter Delete_completes_when_the_provider_reports_a_refused_purge --logger "console;verbosity=minimal" 2>&1 | tail -15`
Expected: PASS for each engine that has a container (it asserts existing behaviour, so there is no red step). If Docker is unavailable the run reports the fixture failure; say so rather than claiming a pass.

- [ ] **Step 3: Falsify it, then restore**

In `src/modules/Themia.Modules.Storage/TenantStorage.cs` line ~206 change `catch (Exception ex) when (ex is not OperationCanceledException)` to `catch (Exception ex) when (ex is not OperationCanceledException and not CdnPurgeException)`, run the same command, confirm the test FAILS with `CdnPurgeException`, then `git checkout src/modules/Themia.Modules.Storage/TenantStorage.cs`.

- [ ] **Step 4: Commit**

```bash
git add tests/Themia.Modules.Storage.IntegrationTests/StorageConformanceTests.cs
git commit -m "test: pin that Modules.Storage swallows a refused CDN purge"
```

---

### Task 5: README, changelog, catalog, and the whole-change check

**Files:**
- Create: `src/neutral/Themia.Storage.Cloudflare/README.md`
- Modify: `CHANGELOG.md` (`[Unreleased]`), `docs/themia-architecture-overview.md` (the `Themia.Modules.Storage` row near line 132 and the neutral package list near line 48)

**Interfaces:** consumes everything above; produces documentation only.

- [ ] **Step 1: Write the package README**

`src/neutral/Themia.Storage.Cloudflare/README.md` (the build packs it automatically when it exists):

````markdown
# Themia.Storage.Cloudflare

Purges a public object's URL from Cloudflare's edge cache after the object is deleted from storage
(coord #0153). `IStorageProvider` stays the seam your code injects; no call site changes.

Packages: the abstraction (`ICdnPurger`, `CdnPurgeException`, `PurgingStorageProvider`) is in `Themia.Storage`;
this package is the Cloudflare implementation.

## Wiring

```csharp
services.AddThemiaStorageCloudflarePurge(o =>
{
    o.Enabled = true;                  // false (the default) registers a no-op purger
    o.ZoneId = "<zone id>";
    o.ApiToken = "<token scoped Zone > Cache Purge on that zone>";   // NOT the R2 key
});

// Split storage: wrap the PUBLIC slot only. Register the inner provider as its own factory-built
// singleton so the container disposes it; Themia never disposes what it wraps.
services.AddSingleton(_ => new S3StorageProvider(publicOptions));
services.AddThemiaSplitStorage(
    sp => new PurgingStorageProvider(sp.GetRequiredService<S3StorageProvider>(), sp.GetRequiredService<ICdnPurger>()),
    sp => new LocalStorageProvider(privateOptions));

// Single provider: wrap it whole.
services.AddSingleton(_ => new S3StorageProvider(options));
services.AddSingleton<IStorageProvider>(sp =>
    new PurgingStorageProvider(sp.GetRequiredService<S3StorageProvider>(), sp.GetRequiredService<ICdnPurger>()));
```

Bind `CloudflarePurgeOptions` from configuration in your app. With `Enabled=true`, a blank `ZoneId` or `ApiToken`
fails host start.

## What a delete does

For a `public/` key: build `inner.GetPublicUrl(key)`, delete the object, then purge that URL. Any other key is
only deleted. A purge Cloudflare refuses, answers with `success:false`, or answers with something that is not its
purge response throws `CdnPurgeException`, which means **the object is deleted and the edge may still serve it**.
Repeat the delete and purge; delete is idempotent. Caller cancellation throws `OperationCanceledException`.
There is no retry inside; the caller owns it (Cloudflare rate-limits purge per account, error code 1134).

## What it does not do

- **`Themia.Modules.Storage` swallows the failure.** `TenantStorage.DeleteAsync` commits the soft-delete and then
  only logs a provider-delete exception, so under that module a refused purge is logged and lost. The "failure
  surfaces" guarantee holds for callers of `IStorageProvider.DeleteAsync` directly.
- Only the exact URL is purged: query-string variants, `/cdn-cgi/image` paths, a Transform-Rule-rewritten URL, and
  objects under a custom cache key built from headers or cookies are not.
- A `200` with `success:true` means Cloudflare accepted the request, not that removal was confirmed.
- A request in flight across the purge can refill the edge for the full TTL.
- The URL's host must be served by the zone; the host's mismatch surfaces as a `CdnPurgeException` on the first delete.
- Not run against the live Cloudflare API in Themia's tests; request and response shapes follow Cloudflare's documentation.

Design and review record: `docs/superpowers/specs/2026-10-03-storage-cdn-purge-design.md`.
````

- [ ] **Step 2: Changelog and catalog**

In `CHANGELOG.md` under `## [Unreleased]` add (create the `### Added` heading):

```markdown
### Added
- **Purge the CDN edge when a public object is deleted** (`Themia.Storage`, new `Themia.Storage.Cloudflare`,
  coord #0153). `PurgingStorageProvider` wraps an `IStorageProvider` (the public slot of a split provider, or the
  single provider): for a `public/` key it builds `GetPublicUrl(key)`, deletes the object, then purges that URL
  through `ICdnPurger`; any other key is only deleted. A refused purge (non-2xx, HTTP 200 with `success:false`,
  an unreadable body, a transport failure, a timeout) throws `CdnPurgeException`, meaning "deleted, the edge may
  still serve it"; repeat the idempotent delete. `Themia.Storage.Cloudflare` supplies `CloudflareCdnPurger`
  (`AddThemiaStorageCloudflarePurge`): off by default, its own Zone / Cache Purge token (never logged), and
  `Enabled` with a blank `ZoneId` or `ApiToken` fails host start. No existing type, option or default changes.
  Under `Themia.Modules.Storage` the purge is best-effort: `TenantStorage.DeleteAsync` logs and swallows a
  provider-delete failure, and this release does not change that.
```

In `docs/themia-architecture-overview.md`, change the neutral package list `Themia.Storage(.S3/.AspNetCore)` to `Themia.Storage(.S3/.AspNetCore/.Cloudflare)` (line ~48) and append to the `Themia.Modules.Storage` catalog row's status cell: `; CDN edge purge on public delete via Themia.Storage.Cloudflare (coord #0153)`. Check the surrounding line with `grep -n 'Themia.Storage(' docs/themia-architecture-overview.md` first and edit only those two places.

- [ ] **Step 3: Whole-change verification**

Run, from the repo root:

```bash
dotnet build src/neutral/Themia.Storage src/neutral/Themia.Storage.Cloudflare -v q --nologo --no-incremental 2>&1 | tail -8
dotnet pack src/neutral/Themia.Storage.Cloudflare -c Release -v q --nologo -o /tmp/themia-pack-check 2>&1 | tail -5
dotnet test tests/Themia.Storage.Tests --logger "console;verbosity=minimal" 2>&1 | tail -6
dotnet test tests/Themia.Storage.Cloudflare.Tests --logger "console;verbosity=minimal" 2>&1 | tail -6
dotnet test tests/Themia.Storage.AspNetCore.Tests --logger "console;verbosity=minimal" 2>&1 | tail -6
```

Expected: 0 warnings and 0 errors on both TFMs; the pack succeeds and `unzip -l /tmp/themia-pack-check/Themia.Storage.Cloudflare.*.nupkg | grep README` shows the README; all three test projects PASS. Report the real counts. Then `graphify update .` (project CLAUDE.md asks for it after code changes).

- [ ] **Step 4: Commit**

```bash
git add src/neutral/Themia.Storage.Cloudflare/README.md CHANGELOG.md docs/themia-architecture-overview.md
git commit -m "docs: document the Cloudflare CDN purge and its limits"
```

---

## Out of scope for this plan (decide with the maintainer)

- Pushing the branch, the docs-only spec PR (as #269 was for #0152), the implementation PR, and the release PR. The user opens or merges those; a release publishes every package to NuGet irreversibly.
- A version bump (PATCH per the changelog policy, additive) happens in the release commit, not here.
- A live Cloudflare run; the first one will be propertiezy's.
- Changing `Themia.Modules.Storage`'s delete contract (spec §8).

## Self-review notes

- **Spec coverage:** §2(a) package split → Tasks 1-2; §2(b) exception → Tasks 1-2 (tests 2, retry); §2(c) composition and dispose-safe wiring → Task 1 (router, container-dispose) and Task 3 (real graph, sole provider); §2(d) token, startup validation, redaction → Task 3; §2(e) nothing in Themia; §3 request shape, 15 s, inert-by-default → Tasks 2-3; §4 API/PublicAPI/README/CHANGELOG/catalog → Tasks 1-5; §6 limits → README; §7 tests 1-9 → Tasks 1-4 (test 5 and 6 in Task 3, test 9 in Task 4); §8 → Task 4 + README.
- **One deliberate refinement of the spec:** `PurgingStorageProvider.DeleteAsync` builds the public URL *before* deleting (the spec says delete-then-purge, which still holds). A public slot with no public container then fails before any bytes are lost; Task 1 has a test and a falsifier for it. Mention it in the review hand-off.
- **Types are consistent across tasks:** `ICdnPurger.PurgeAsync(Uri, CancellationToken)`, `CdnPurgeException(Uri, string, int?, Exception?)`, `PurgingStorageProvider(IStorageProvider, ICdnPurger)`, `CloudflareCdnPurger(IHttpClientFactory, IOptions<CloudflarePurgeOptions>)`, `CloudflareCdnPurger.HttpClientName`, `ApiBaseAddress`, `AddThemiaStorageCloudflarePurge(this IServiceCollection, Action<CloudflarePurgeOptions>)`.
