# Themia.Storage CDN purge — delete, then clear the edge copy

**Status:** design, not built. Coord #0153 (accepted 2026-10-03 as "to be designed, then released").
**Consumers:** propertiezy (needs it now; has a local reference version), ezy-assets (intends to use it when
photos move to R2; no delete flow yet).
**Builds on:** the #0152 split shipped in 0.30.2 (`SplitStorageProvider`, `AddThemiaSplitStorage`).

## 1. What is asked, and what is true in it

Behind a CDN, `IStorageProvider.DeleteAsync` removes the object from the bucket and nothing else; the edge
keeps serving it until its TTL ends. propertiezy caches for a year on purpose (`immutable`, unique keys), so a
photo a seller removes stays visible at a permanent unsigned URL. (The request also named a listing taken down
for a claims violation; propertiezy checked its callers and a takedown suppresses the listing without deleting
its photo objects, so purge-on-delete covers seller-initiated removal only.) Themia owns the `public/`
rule and `GetPublicUrl(key)`, which is exactly the URL the edge cached, so the delete-then-purge pair belongs
here rather than in each app.

Checked against the code, not taken from the request:

- **`DeleteAsync` is idempotent in both backends.** Local checks `File.Exists` before deleting
  (`Themia.Storage/Local/LocalStorageProvider.cs:111`); S3 issues `DeleteObject`, which succeeds on a missing
  key (`Themia.Storage.S3/S3StorageProvider.cs:118`). So "retry the delete+purge pair" (request, point 2) is
  safe. R2's behaviour on a missing key is assumed S3-compatible and not verified; test 8 uses Local.
- **The router already routes by `public/` prefix** and `GetPublicUrl` on a non-public key throws
  (`SplitStorageProvider.GetPublicUrl`). A decorator can reuse `StorageKey.IsPublic` with no new rule.
- **`SplitStorageProvider` probes the public slot at construction** by calling `GetPublicUrl("public/_probe")`,
  so a decorator over the public slot must forward `GetPublicUrl` untouched.
- **`Themia.Modules.Storage` swallows provider-delete failures** (`TenantStorage.cs:201-209`: the logical
  delete is committed first, then `provider.DeleteAsync` runs in a `catch (Exception) { LogWarning }`). Under
  that module a refused purge cannot surface. See §8; this is the one finding the request did not anticipate.
- **`Themia.Modules.Storage` gives no way to hand it a decorated provider.** `StorageBuilder.UseS3`/`UseR2`
  register a bare `S3StorageProvider` (`DependencyInjection/StorageServiceCollectionExtensions.cs:84,104`);
  `AddThemiaStorage` itself does not register an `IStorageProvider`, so a module user wires the decorator by
  registering `IStorageProvider` themselves instead of calling `UseS3`/`UseR2`.

Checked against Cloudflare's documentation (fetched 2026-10-03), not taken from the request:

- **Request and response shape.** `POST /zones/{zone_id}/purge_cache`, `Authorization: Bearer`, permission
  `Cache Purge`; response envelope `{ "errors": [{ "code", "message" }], "messages", "result", "success" }`, with
  `success:false` and a populated `errors` array on a refusal (documented examples: code 1092 for a bad body,
  code 1134 "rate limit reached").
- **`200` + `success:true` means accepted, not removed.** Cloudflare says so in as many words; it purges every
  data center and cache tier, including Cache Reserve, and does not serve purged content again afterwards.
- **The request was wrong on one number:** purge-by-URL takes up to **100** URLs per request on
  Free/Pro/Business (500 Enterprise), not 30. Single-file purge is rate-limited per account (Free: 800 URLs/s).
- **Not found in the documentation:** what happens when a URL's host is not in the zone (§6).

## 2. Decisions (the five questions from #0153 [3])

**(a) Package placement.** Split along the dependency line, as S3 and AspNetCore already are:

| Type | Package | Why |
|---|---|---|
| `ICdnPurger`, `CdnPurgeException`, `PurgingStorageProvider` | `Themia.Storage` | No new dependencies; the abstraction must not hard-code Cloudflare, and a second CDN reuses all three. |
| `CloudflarePurgeOptions`, `CloudflareCdnPurger`, DI extension | new `Themia.Storage.Cloudflare` (`net8.0;net10.0`) | Keeps a vendor client and its credential out of core, and carries `Microsoft.Extensions.Http` for the DI helper; apps without a CDN never see it. |

The package name says what the vendor does for us (CDN purge), not R2: R2 stays covered by `Themia.Storage.S3`.

*Alternative considered:* `CloudflareCdnPurger(HttpClient, options)` inside `Themia.Storage`, no new package, BCL
types only. It adds no dependency, so the split is a placement choice, not a necessity: shipping a vendor
implementation and its credential to every Storage adopter is the cost, and a factory-managed handler lifetime
(connection rotation for a long-lived singleton) is what the extra dependency buys. The maintainer may prefer
the single-package form; Q1 asks the consumers.

**(b) Exception type.** A sealed `CdnPurgeException : Exception` in `Themia.Storage`, with `Uri Url` and
`int? HttpStatus` (null for a transport failure or an unreadable body). It means exactly one thing: *the
delete succeeded and the cache may still hold the object.* A failed delete throws whatever the inner provider
throws, unchanged, so the two are told apart by type. Raised for: any non-2xx; HTTP 200 with `success:false`;
HTTP 2xx whose body cannot be read as the Cloudflare envelope (success cannot be confirmed, so it is a failure);
`HttpRequestException`; `HttpClient`'s own timeout. Not raised for caller cancellation: an
`OperationCanceledException` on the caller's token propagates as itself. The message names the URL and the
Cloudflare error code and message (first error only, length-capped), never the response body. The caller's
recovery is the same for every exception out of `DeleteAsync`: repeat the delete and purge.

The decorator enforces that meaning rather than trusting every `ICdnPurger`: `PurgingStorageProvider.DeleteAsync`
reports any failure of the purge step after a successful delete as `CdnPurgeException` (the original exception
kept as `InnerException`; the message names its type and the URL, never its text). So an app-wide `HttpClient`
handler (a resilience or retry handler added with `ConfigureHttpClientDefaults` applies to this named client too)
or a purger built without its named client cannot leak another exception type after the object is gone. A
`CdnPurgeException` from the purger passes through as the same instance; the caller's own cancellation still
propagates as `OperationCanceledException`; the delete itself is outside that catch. (Added during
implementation, after the final review found the gap.)

**Scope of that guarantee:** it holds for a caller that calls `IStorageProvider.DeleteAsync` on the decorated
provider (propertiezy's queued job). It does **not** hold under `Themia.Modules.Storage`, which catches and logs
it (§8). The README of the new package says so.

No retry inside the purger. A 429 or 5xx surfaces and the caller (propertiezy: its queued job) owns the retry
policy; two retry layers would multiply attempts against a rate limit.

**(c) Composition with the router.** `PurgingStorageProvider(IStorageProvider inner, ICdnPurger purger)` is a
plain decorator and never disposes `inner`, the same rule `AddThemiaSplitStorage` states for slots: whoever
constructs the inner provider owns it. So register the inner provider as its own factory-built singleton (the
container disposes those; it does not dispose an instance you pass in) and resolve it inside the slot factory,
rather than constructing it there, where nothing would dispose it. Two supported wirings, both through the seam
apps already inject:

```csharp
// split: wrap the PUBLIC slot only
services.AddThemiaStorageCloudflarePurge(o => { o.Enabled = true; o.ZoneId = ...; o.ApiToken = ...; });
services.AddSingleton(_ => new S3StorageProvider(publicOpts));   // factory form: the container disposes it
services.AddThemiaSplitStorage(
    sp => new PurgingStorageProvider(sp.GetRequiredService<S3StorageProvider>(), sp.GetRequiredService<ICdnPurger>()),
    sp => new LocalStorageProvider(privateOpts));

// single provider: wrap it whole
services.AddSingleton(_ => new S3StorageProvider(opts));
services.AddSingleton<IStorageProvider>(sp =>
    new PurgingStorageProvider(sp.GetRequiredService<S3StorageProvider>(), sp.GetRequiredService<ICdnPurger>()));
```

A `Themia.Modules.Storage` user registers `IStorageProvider` the same way (instead of `UseS3`/`UseR2`) and gets
best-effort purging only (§8).

`PurgingStorageProvider.DeleteAsync` normalises the key (`StorageKey.NormalizeAndValidate`, so `public\x` is
treated as public and `Public/x` as private, as the router does), and for a `public/` key builds
`inner.GetPublicUrl(key)` first (so a public slot with no public container fails before anything is deleted),
calls `inner.DeleteAsync`, and only then calls `purger.PurgeAsync(url)`. Every other member forwards. No
`AddThemiaPurging…` convenience wrapper: the lambda above is the whole wiring and a helper would hide which
slot is wrapped.

**(d) Token and startup validation.**
`CloudflarePurgeOptions { bool Enabled (false); string ZoneId; string ApiToken; }`. The token is its own
credential, scoped Zone → Cache Purge on one zone, and has no relation to the R2 keys in `S3StorageOptions`.
`AddThemiaStorageCloudflarePurge(Action<CloudflarePurgeOptions>)` validates on start: `Enabled` with a blank
`ZoneId` or `ApiToken` fails the host boot (`ValidateOnStart`, the Geo.Google pattern); `Enabled=false` with
blanks boots. The token travels only in the `Authorization: Bearer` header, set per request, never in a URL, a
message, an exception, or a default header. The named client calls `RedactLoggedHeaders` for `Authorization`
explicitly. The framework's default (Microsoft.Extensions.Http 10.0.9) already redacts header values: removing the
explicit call left the no-leak test green when tried. So the call is defense in depth, kept so the token's safety
does not depend on a default that can change, and pinned by its own test (that test makes the default
non-redacting with `ConfigureAll`, so removing the call fails it). The client keeps request logging otherwise (useful to operators; the token is not in the URI). Test 5
proves the token is absent from Trace-level output and fails if redaction is overridden, so the test can see a
leak. `ZoneId` is escaped as a path segment (`Uri.EscapeDataString`) and not format-checked.

**(e) A consumer with no delete flow (ezy-assets).** Nothing in Themia changes for them. Photo removal is a
new ezy-assets command; when they write it they call `IStorageProvider.DeleteAsync` (or whatever their seam
wraps) on the wired provider and handle `CdnPurgeException` as "deleted, retry". This spec only has to make
that adoption a configuration change: the wiring in (c) plus `Enabled`. Their no-CDN V1 stays untouched
because the feature is off unless they call the extension and set `Enabled`.

## 3. Behaviour

Inert by default: nothing is registered until `AddThemiaStorageCloudflarePurge` is called, and with
`Enabled=false` it registers a no-op purger, so an app can wire the decorator unconditionally and switch it per
environment (Local dev: off; production: on). No HTTP call is made while disabled.

Request, per deleted public key: `POST https://api.cloudflare.com/client/v4/zones/{ZoneId}/purge_cache`,
`Authorization: Bearer {ApiToken}`, body `{"files":["<inner.GetPublicUrl(key)>"]}`, from a named `HttpClient`
(`Themia.Storage.Cloudflare`) with a fixed 15 s timeout (a chosen default, not a Cloudflare figure: a purge sits inside a delete and the
default 100 s would stall a queue worker). The URL purged is the string the slot returns, so it is the URL the edge was asked for.
The shape matches Cloudflare's documented purge-by-URL request (§1); the body is documented to accept a `files`
array of URLs.

`ICdnPurger` is `Task PurgeAsync(Uri url, CancellationToken cancellationToken = default)`, single URL.
A batch (documented limit 100 URLs per request, §1) is not needed: propertiezy deletes one object at a time. If it is wanted later it
arrives as a default interface member, so an adopter's own `ICdnPurger` test double does not stop compiling.

## 4. Public API

`Themia.Storage` (additive): `ICdnPurger`, `CdnPurgeException`, `PurgingStorageProvider`.
`Themia.Storage.Cloudflare` (new): `CloudflarePurgeOptions`, `CloudflareCdnPurger`,
`StorageCloudflareServiceCollectionExtensions.AddThemiaStorageCloudflarePurge`. PublicAPI files for both, a
README for the new package, `CHANGELOG.md` `[Unreleased]` (PATCH: additive, per the changelog policy), the
package row in `docs/themia-architecture-overview.md`. No `IStorageProvider` member is added, so no adopter's
own provider or fake changes.

## 5. Back-compat

Additive only. No existing type, option, registration or default changes. An app that does not call the new
extension has no new dependency, no new service and no new HTTP call.

## 6. Known differences and limits (said, not hidden)

- **Only the exact URL is purged.** Query-string variants (`?w=300`) and URL-path transforms
  (`/cdn-cgi/image/...`) are separate cache entries and are not purged. propertiezy and ezy-assets are asked
  whether they serve any (§9, Q4); if so that is a follow-up, not a silent gap.
- **Hostname must be in the zone (expectation, not verified).** `ZoneId` is the zone that serves
  `PublicBaseUrl`'s host. The Cloudflare documentation read for this design does not say what a purge for a host
  outside the zone returns, so the design assumes a refusal and surfaces whatever comes back as a
  `CdnPurgeException` on the first delete, carrying Cloudflare's code and message. Themia cannot see this at
  startup.
- **"Accepted" is not "confirmed gone".** A `200`/`success:true` means Cloudflare accepted the request (§1);
  confirming removal would mean requesting the URL and reading `CF-Cache-Status: MISS`, which this design does
  not do.
- **A request in flight across the purge can refill the edge.** If an edge node fetched the object from origin
  just before the delete and caches the response just after the purge, the object is cached again for the full
  TTL. Delete-then-purge narrows that window; it does not close it. A second, delayed purge is the caller's
  decision.
- **Custom cache keys and Transform Rules.** Cloudflare documents that a custom cache key built from headers or
  cookies needs those values in the purge request, and that a URL rewritten by a Transform Rule must be purged
  by its end-user URL. This design purges `GetPublicUrl(key)` with a `files` array of one URL and no headers.
  Q4 asks whether either applies.
- **Rate limits surface.** Cloudflare limits purge per account (error code 1134 in its examples); that arrives as
  a `CdnPurgeException` and the caller throttles its retry.
- **No ordering across multiple deletes.** One key, one delete, one purge.

## 7. Tests (written failing first; each names its falsifier)

1. **Order.** Spy inner + spy purger record calls: delete precedes purge. A throwing inner delete means the
   purger is never called. *Falsifier:* swap the two lines and watch both fail.
2. **Refusal surfaces as `CdnPurgeException`**, with a stub `HttpMessageHandler`: 429, 500, 200 `success:false`,
   a 4xx carrying Cloudflare's rate-limit envelope (code 1134), 200 with an empty body, 200 with non-JSON, `HttpRequestException`, `HttpClient` timeout. A cancelled caller
   token yields `OperationCanceledException`, not `CdnPurgeException`. *Falsifier:* check the status code only
   and show the `success:false` case pass silently. The timeout case is a stub handler throwing
   `TaskCanceledException` with the caller's token not cancelled (the shape `HttpClient`'s own timeout
   produces), not a sleep.
3. **Only public keys.** `x`, `private/x`, `Public/x`, `_platform/x`: purger untouched, delete forwarded.
   `public\x` is normalised to `public/x` and purged once. *Falsifier:* purge every key and count the calls.
4. **Request shape.** Method, path with `ZoneId` escaped, `Authorization: Bearer`, body `{"files":[url]}` equal to
   `inner.GetPublicUrl(key)` for a key with a space and a non-ASCII character. *Falsifier:* build the URL as
   `base + key` unescaped and show the body differ from the slot's URL.
5. **Token never leaks.** Capture all log output at Trace for a successful and a failed purge, plus
   `CdnPurgeException.ToString()` and `CloudflarePurgeOptions` formatting: no occurrence of the token.
   *Falsifier:* set `ShouldRedactHeaderValue` to `false` on the named client after ours and show the same
   assertion fail.
6. **Startup validation and inertness, through the real container.** `Enabled` with blank `ZoneId`, then blank
   `ApiToken`: the host fails at start. `Enabled=false` with blanks starts; a public delete makes zero HTTP calls;
   defaults alone (extension called, nothing configured) start. *Falsifier:* validate on first resolve and show
   the host starts.
7. **Composition with the router, through the default DI graph** (`AddThemiaSplitStorage` factory overload plus the
   new extension, not hand-built): public delete → purge; private delete → none; the construction probe passes
   through the decorator; disposing the container does not dispose the inner slot. A second case wires the
   decorator as the sole `IStorageProvider`. *Falsifier:* a decorator that does not forward `GetPublicUrl` makes
   the router constructor throw; assert that it does.
8. **Retry is safe.** Real `LocalStorageProvider` + stub purger that refuses once: first `DeleteAsync` throws
   `CdnPurgeException` with the object already gone; the second completes and purges. *Falsifier:* make the
   decorator purge before it deletes and show this test and test 1 both fail.
9. **Characterisation of the module limit (§8).** `TenantStorage` over the decorator with a throwing purger:
   the logical delete completes and a warning is logged, no exception. This pins today's behaviour so changing
   it is a deliberate decision. *Falsifier:* narrow the module's catch so `CdnPurgeException` escapes and show this test fail.

10. **A purger failure of any type after the delete** (added during implementation, §2(b)): an
    `InvalidOperationException` from the purger arrives as `CdnPurgeException` with the original as
    `InnerException`; a `CdnPurgeException` passes through as the same instance; caller cancellation stays
    `OperationCanceledException`. *Falsifier:* remove the catch, wrap `CdnPurgeException` too, or drop the
    cancellation guard; each fails a named test. Test 4 also pins the `ZoneId` path escaping (`zone/1 x`).

Existing storage tests are the guard for §5 and are not edited.

## 8. Open for the maintainer: `Themia.Modules.Storage`

`TenantStorage.DeleteAsync` commits the soft-delete, then calls the provider in a catch-all that logs a warning
(`TenantStorage.cs:~188-209`; the comment promises "a future reconcile sweep"). **No such sweep exists:** a search
of `Themia.Modules.Storage` finds the comment and the log message, no implementation (checked 2026-10-03). If the
decorator is the provider under that module, a refused purge is logged and lost, the row is already gone, and no
caller can retry it. That contradicts request point 2 for module users, and `UseR2` is the module's own route for
R2 users. This design does **not** change the module: its best-effort contract is documented and tested, and
changing it trades one guarantee for another.

Q6 is answered (§11): ezy-assets will call the provider directly and not use the module, and propertiezy
already does. So no known consumer is affected, and the maintainer's choice is between (i), the cheapest, and
the rest, which only matter if a module user appears.

Options:

- (i) Leave it and document that, under the module, the purge is best-effort and a failure is only logged.
- (ii) Let `CdnPurgeException` escape the module's catch. The caller then learns of the failure, but the row is
  already soft-deleted and the call cannot be repeated with the same key.
- (iii) Build the reconcile sweep, which is new work and out of scope here.
- (iv) Reorder the module (blob delete and purge before the commit). It brings back an external call before a
  transaction commit, and changes a documented ordering.

This is the same pattern as an opt-in fix that misses a caller, so the README states the scope of the guarantee
(§2(b)) whichever option is chosen.

## 9. Questions to the consumers (same list to both)

1. Package split (a), or the single-package alternative, and the names `Themia.Storage.Cloudflare` /
   `ICdnPurger` / `CdnPurgeException`: agree?
2. Exception contract (b): does your retry path want to branch on `CdnPurgeException` ("deleted, cache stale")
   versus any other exception ("delete failed"), or treat both as retry? Anything you need on the exception
   (`Url`, `HttpStatus`) that is missing?
3. Wiring (c), a hand-constructed decorator inside the slot factory, never disposed by Themia: workable?
4. Does the edge cache any URL for the same object other than `GetPublicUrl(key)` (query-string variants,
   image-resizing paths)? If yes, say which.
5. propertiezy: what did your working purger see on rate limiting and on an unreachable Cloudflare, and does
   your retry job already back off? (Shape and limits are checked against Cloudflare's docs, §1.)
6. ezy-assets: will photo removal go through `Themia.Modules.Storage` (§8) or through the provider directly?
   This answer decides §8.

## 10. Not covered, and said so

- Other CDNs. The abstraction does not hard-code Cloudflare; no second implementation is written.
- Batch purge, purge by tag/prefix, purge-everything.
- Purging variants of a URL (§6).
- Config binding in Themia; the app binds `CloudflarePurgeOptions` as it binds every other options type.
- Changing `Themia.Modules.Storage`'s delete contract (§8).
- A run against the live Cloudflare API. Neither consumer has one (propertiezy's interim purger has only
  stub-handler tests; ezy-assets has no CDN yet), so request shape and limits are verified against the
  documentation only. The first real purge will be propertiezy's, once `img.propertiezy.com` is bound.
- A `CdnPurgeException` property for Cloudflare's first error code. propertiezy suggested it as optional (they
  would log it, not depend on it); the code is already in the message, and adding a property later is
  additive, so it is not added now.

## 11. Consumer review (2026-10-03)

Both consumers' sessions read the spec over cross-session messages; these are those sessions' readings, not their
users' decisions, and nothing was built or run.

- **ezy-assets** (read the whole spec): agree 1, 2, 3, 6; Q4 partial (no variant URLs in code; Cloudflare-side
  config unknown, nothing set up yet). Not on `Themia.Storage` today: their seam is their own `IFileStorage`
  with no delete and generated paths, so an adapter comes first. Their migration, not Themia's.
- **propertiezy** (read §2-§6 only, not §1 or §7-§10): agree 1, 3, 4; do not branch on exception type (their
  drain job catches `Exception` per row and retries the pair); no second URL known; nothing observed on rate
  limits (a 429 means up to 50 attempts per 5-minute pass, throttling theirs to add if it matters). Two changes
  came from them: the takedown correction (§1) and the dispose-safe wiring (§2(c)).
