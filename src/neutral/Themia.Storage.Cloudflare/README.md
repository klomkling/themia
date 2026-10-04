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

**Wrap once.** Wrap the public slot or the sole provider, never both: wrapping both purges every delete twice, and a
refused second call reports a false "cache stale".

**Two S3 slots.** The split example registers `S3StorageProvider` by its concrete type. If both slots are S3 (for
example R2 for public and another bucket for private), register them as keyed services (`AddKeyedSingleton`)
instead: by concrete type the last registration wins and both slots resolve to the same bucket. A slot built inside
the `AddThemiaSplitStorage` lambda is never disposed by Themia, which is why the example registers the inner provider
as its own factory-built singleton.

Bind `CloudflarePurgeOptions` from configuration in your app. With `Enabled=true`, a blank `ZoneId`, or an `ApiToken` that is blank or contains whitespace or control characters (trim a trailing newline read from a secret file),
fails host start.

## What a delete does

For a `public/` key: build `inner.GetPublicUrl(key)`, delete the object, then purge that URL. Any other key is
only deleted. A purge Cloudflare refuses, answers with `success:false`, or answers with something that is not its
purge response throws `CdnPurgeException`, which means **the object is deleted and the edge may still serve it**.
Repeat the delete and purge; delete is idempotent. Any other failure of the purge step after the delete (for
example a resilience handler's `BrokenCircuitException`) is also reported as `CdnPurgeException`, with the original
as `InnerException`. Caller cancellation throws `OperationCanceledException`.
There is no retry inside; the caller owns it (Cloudflare rate-limits purge per account, error code 1134).

## What it does not do

- **`Themia.Modules.Storage` swallows the failure.** `TenantStorage.DeleteAsync` commits the soft-delete and then
  only logs a provider-delete exception, so under that module a refused purge is logged and lost. The "failure
  surfaces" guarantee holds for callers of `IStorageProvider.DeleteAsync` directly.
- **App-wide `HttpClient` defaults apply.** The Cloudflare client is a named client, so handlers added with
  `ConfigureHttpClientDefaults` (resilience, retry) also apply to it. A retry handler means more than one POST per
  purge, and any failure it raises is reported as `CdnPurgeException` by the decorator.
- Only the exact URL is purged: query-string variants, `/cdn-cgi/image` paths, a Transform-Rule-rewritten URL, and
  objects under a custom cache key built from headers or cookies are not.
- A `200` with `success:true` means Cloudflare accepted the request, not that removal was confirmed.
- A request in flight across the purge can refill the edge for the full TTL.
- Only a delete purges. Overwriting an object at an existing `public/` key leaves the old content at the edge for the full TTL, so use a new key per upload (propertiezy and ezy-assets do) or delete first.
- The URL's host must be served by the zone. What Cloudflare answers for a host outside the zone is not documented in
  what Themia reviewed, so a mismatch is expected to surface as a `CdnPurgeException` on the first delete, carrying
  Cloudflare's code and message, but that is not verified.
- Not run against the live Cloudflare API in Themia's tests; request and response shapes follow Cloudflare's documentation.

Design and review record: `docs/superpowers/specs/2026-10-03-storage-cdn-purge-design.md`.
