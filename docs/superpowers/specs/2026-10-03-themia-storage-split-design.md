# Themia.Storage split — one app, a public slot and a private slot

**Status:** design, revision 1 — implemented (coord #0152)
**Raised by:** coord #0152 (propertiezy); second consumer confirmed by ezy-assets on the same thread.
**Precedent:** #0147 (key prefix and `Visibility` must agree on a single provider), #0134 (Local presigned downloads).

**Governing constraint:** the routing rule already exists inside every single provider — the first key
segment `public/` selects the public container. This design adds a router over two providers and
nothing else. It adds no routing rule, no new key shape, and changes no behaviour of a configuration
that does not opt in.

---

## 1. What is asked, and what is true in it

An app gets one `IStorageProvider`, so `Storage:Provider=S3` would move identity documents to R2
together with listing photos. Propertiezy's record of processing says those documents stay on a server
in Thailand; photos are already disclosed as on Cloudflare R2. The two kinds of object need different
backends. ezy-assets has the same pair (property photos; signer IDs, signatures, sealed contracts) and
confirmed it.

Verified against source rather than taken on report:

- The routing marker exists: `StorageKey.IsPublic` is `StartsWith("public/", Ordinal)`; private keys are
  unprefixed.
- Reads (`Get`/`Exists`/`Stat`/`Delete`/`GetPresignedUrl`/`GetPublicUrl`) carry only a key. Only
  `PutAsync` carries `StoragePutOptions.Visibility`, and since f9a98b3 it must agree with the key.
- `StorageUrlService` wraps one `IStorageProvider`, returns an absolute presigned URL untouched and joins
  a relative one onto `PresignedBaseUrl`. It is provider-agnostic, so it needs no change.
- Themia binds **no** `Storage:*` configuration. `Storage:Provider` is an app-level switch in each
  consumer. See §6 — this corrects what was written on #0152 [3] item 5.

## 2. The routing rule (decided on #0152 [3])

**Route on the key. The declared `Visibility` is a mandatory cross-check on writes.**

| operation | slot chosen by |
| --- | --- |
| `PutAsync` | the key; `options.Visibility` must agree, else `ArgumentException`, nothing reaches either slot |
| `GetAsync` `ExistsAsync` `StatAsync` `DeleteAsync` | the key |
| `GetPresignedUrlAsync` | the key |
| `GetPublicUrl` | public slot only; a non-`public/` key throws `InvalidOperationException` without consulting a slot |

`GetPublicUrl` on a non-public key throws `InvalidOperationException` in single-provider mode too
(`LocalStorageProvider` and `S3StorageProvider` both do), so the router adds no behaviour there. An app that
calls it on a stored value of unknown shape (propertiezy: `ListingPhotoUrl.Resolve` over any stored
non-http value) has that exposure today and keeps it unchanged.

Public iff the **normalised** key starts with `public/`. Anything else is private — it fails closed.

**Normalise before classifying.** `public/../x` starts with `public/`; a raw test sends it to the public
slot. The router calls `StorageKey.NormalizeAndValidate` first (rejects `..` and absolute paths, converts
`\` to `/`), classifies the normalised key, and passes that same normalised key to the slot, so the
classification and the stored key cannot differ. `Public/x` (capital) is private, as it is today.

The slot receives the key **unchanged** (still `public/`-prefixed for the public slot). Each slot's own
`EnsureMatchesVisibility` and prefix-stripping keep working; the router is a dispatcher, not a rewriter.

## 3. The type

```csharp
// Themia.Storage (neutral). Sealed. Owns both slots.
public sealed class SplitStorageProvider : IStorageProvider, IDisposable
{
    public SplitStorageProvider(IStorageProvider publicSlot, IStorageProvider privateSlot);
}

public static IServiceCollection AddThemiaSplitStorage(
    this IServiceCollection services, IStorageProvider publicSlot, IStorageProvider privateSlot);

public static IServiceCollection AddThemiaSplitStorage(
    this IServiceCollection services,
    Func<IServiceProvider, IStorageProvider> publicSlot, Func<IServiceProvider, IStorageProvider> privateSlot);
```

**Why a factory overload.** Both consumers build providers through DI, not by hand: propertiezy through the
options pipeline (`Bind` + `PostConfigure` defaulting `RootPath`/`PublicRootPath`, a >=32-character
`SigningKey` check, `ValidateOnStart`, the same `IOptions<LocalStorageOptions>` read again for the signer and
the static-files mount); ezy-assets as lazily resolved singletons in its registrar. An instance-only form
would make each rebuild that outside DI at registration time. A factory defers construction to first resolve,
which would bring back the validate-at-first-resolve gap, so the factory overload also forces construction at
host start through the same `ValidateOnStart` mechanism `AddThemiaStorageUrls` already uses. That mechanism is
confirmed: `SplitStorageHostTests` boots a host whose slot factory throws and start-up fails, on net8.0 and
net10.0 (falsifier: without `ValidateOnStart` the test fails, and a plain lazy registration of the same slot
starts fine). It needs the generic host; without one nothing resolves the router until first use. Raised by propertiezy on PR #269.

The constructor refuses (at construction, so before the host serves a request):

- either slot `null`, or both slots the same instance — the split would be a lie;
- a public slot with no public container: it calls `publicSlot.GetPublicUrl("public/_probe")`, which the
  contract documents as pure composition with no I/O and which throws when no container is configured.

`Dispose` disposes the slots that implement `IDisposable` (`S3StorageProvider` owns its client). The
extension registers the router so the container disposes it, and throws if an `IStorageProvider` is
already registered, the same "exactly one" guard `Themia.Modules.Storage`'s builder has.

Not in v1: a `Themia.Modules.Storage` `StorageBuilder.UseSplit`. Its tenant scoping already emits
`public/<tenant>/<key>`, so the keys fit; the builder is added when a consumer asks, not before.

## 4. Question 1 — S3 on the public side with no private bucket

Propertiezy measured on 0.23.1; re-verified on this tree: `new S3StorageProvider(options)` calls
`ThrowIfNullOrWhiteSpace(options.BucketName)` and `options.Validate()` rejects
`PublicBucketName == BucketName`. Under public=S3, private=Local the S3 slot is only ever sent `public/`
keys, yet it demands a private bucket that must exist and stay empty. Wrong; a bucket that must be
protected from a tidy-up and never used is cost with no purpose.

**Decision: an explicit `S3StorageOptions.PublicOnly` flag.**

- `PublicOnly = true` requires `PublicBucketName` + `PublicBaseUrl`, and `BucketName` must be **blank** —
  a non-blank value is contradictory (someone pasted the private bucket and believes it is used) and throws.
- In that mode any private key reaching the provider throws `InvalidOperationException` ("public-only").
  The router never sends one; this is defence in depth.
- Public keys behave exactly as today (`Resolve` already maps `public/` to the public bucket).

Why a flag and not "blank `BucketName` is allowed when `PublicBucketName` is set": that loosens a guard on
every existing single-provider config. An adopter who forgot `BucketName` would today fail at startup and
would instead fail at the first private write in production. The flag keeps that startup failure for every
configuration that does not opt in.

## 5. Question 2 — how an app learns its private side is Local

What the code does today (`LocalStorageEndpoints.cs`): `MapThemiaLocalStorage` does **not** inspect the
provider's type. At map time it requires a registered `LocalUrlSigner` (throws if absent) and a
`PresignedBaseUrl` ending in the mount. At request time it verifies the token with the signer and calls
`IStorageProvider.GetAsync(key)`. The type test `GetService<IStorageProvider>() is LocalStorageProvider`
is propertiezy's own, and it is the brittle part: a router is not a `LocalStorageProvider`, the test goes
false, the route is never mounted, and every Local presigned link answers 404 while storage keeps working.

Decision (agreed with both consumers, #0152 [9]), and a test:

1. **The router exposes no `Public` / `Private` properties.** A public property cannot be taken back and the
   question is answered without it.
2. **Mount `MapThemiaLocalStorage` when a `LocalUrlSigner` is registered**, not on a provider type. This
   answers "is the PRIVATE slot Local?" only. An app that also serves the Local PUBLIC container itself
   (propertiezy: `UseStaticFiles` on `PublicRootPath`) must gate that on its own condition — "is the PUBLIC
   slot Local?" — which is false when the public slot is S3/R2. One condition does not cover both (raised by
   propertiezy, #0152 [8]). An app that serves Local presigned links must register the signer anyway —
   `MapThemiaLocalStorage` refuses to start without one — so the condition costs nothing and survives any
   future wrapping of the provider. propertiezy registers the signer only in its Local branch (`Program.cs:417`), and gates its
   Local PUBLIC static-files block (`:990-1007`) on a separate condition — checked by its session against its
   own code on PR #269. ezy-assets registers no signer and does not use `MapThemiaLocalStorage` (no
   `Themia.Storage*` reference in its `src`, tests or `Directory.Packages.props`), so it is a decision at its
   migration.
3. **A Themia test that fails if the link stops opening:** `WebApplicationFactory` host with a split
   provider (Local private, a fake absolute-URL public slot), `AddThemiaStorageUrls`, `MapThemiaLocalStorage`;
   mint a download URL for a private key through `IStorageUrlService`, `GET` it, expect 200 and the bytes.
   Falsifier: remove the mount, expect 404.

Safety of mounting over a router: a token is an HMAC over `key|operation|expiry` (`LocalUrlSigner.Compute`). Only the Local slot mints
tokens, only for private keys, so `/_local/get?key=public/x` cannot be authorised by a token minted for
another key. The route serving a `public/` key through the router would 403 without a valid token.

## 6. Configuration — a correction to #0152 [3]

[3] item 5 said the config shape `Storage:Public:Provider` + `Storage:Private:Provider` was "adopted as-is"
and that validation runs via `ValidateOnStart`. That over-reached: **Themia parses no `Storage:*` keys**,
so there is nothing for it to adopt or validate. What Themia ships is the router and its registration;
choosing slots from configuration stays in the app, exactly as `Storage:Provider` does today. Both
consumers' proposed shape is a good convention and is documented as an example, not enforced.

Fail-fast is delivered differently: the app builds both slot instances in `Program.cs`, and
`SplitStorageProvider` validates in its own constructor. All of it runs before the host starts, so a
half-configured side fails at boot rather than at first resolve. This is the gap ezy-assets flagged on
#399 (validation at first resolution), closed **for apps that construct their slots at boot, or use the
factory overload of §3 which forces construction at start**. ezy-assets builds its adapters as lazily resolved
singletons today, so at migration it must do one or the other; #399 itself keeps first-resolve validation.

**The slot constructors do not all validate, and the earlier draft of this paragraph said they did.**
`new S3StorageProvider(options)` does (blank `BucketName` and `Validate()`). `new LocalStorageProvider(options)`
does **not**: it checks only that `RootPath` is non-blank, and a blank `SigningKey` silently leaves the
signer null, so the first presigned link fails at request time rather than at boot. `LocalStorageOptions.Validate()`
exists and requires `SigningKey`; `Themia.Modules.Storage`'s `UseLocal` calls it explicitly, which is why the
module never showed this. In split mode the **app must call `local.Validate()` before constructing the Local
slot** (the example below does). Changing the Local constructor to call it would break every single-provider
Local config that never presigns, so it is not changed here. Requirement 4 of #0152 ("missing Local signing key
fails fast") is therefore met by that call, not by the router.

The documented example:

```csharp
IStorageProvider Build(string provider, IConfigurationSection section) => provider switch
{
    "S3" => new S3StorageProvider(section.Get<S3StorageOptions>()!),
    "Local" => NewLocal(section.Get<LocalStorageOptions>()!),
    _ => throw new InvalidOperationException($"Unknown storage provider '{provider}'."),
};
LocalStorageProvider NewLocal(LocalStorageOptions local)
{
    local.Validate(); // the Local constructor does not; this is what makes a missing SigningKey fail at boot
    return new LocalStorageProvider(local);
}
// Both Storage:Public and Storage:Private set -> split. Neither -> the single-provider path, unchanged.
// Exactly one -> throw, naming the key.
```

## 7. Back-compat

- A config that registers one provider is untouched: no new type is involved, no existing guard loosens.
- `S3StorageOptions.PublicOnly` defaults to `false`; with it `false` the constructor and `Validate()` behave
  exactly as today.
- Additive public API only: `SplitStorageProvider`, `AddThemiaSplitStorage`, `S3StorageOptions.PublicOnly`.
  `PublicAPI.Unshipped.txt` entries for each. Ships in the next minor; no version is promised here.
- Own test doubles: `IStorageProvider` gains no member, so no consumer fake breaks.

### Known differences from single-provider mode (split mode is opt-in, so none reaches an existing config)

- **The S3 provider never normalises keys.** `NormalizeAndValidate` is called only by `LocalStorageProvider`
  (`LocalStorageProvider.cs:228`). Today an S3 provider stores `a\b`, `/x` and `a/../b` as those literal
  keys, and `public/../x` goes to the public bucket as `../x`. The router normalises and rejects, so on an
  S3 slot a key that worked in single-provider mode can be rejected or re-spelled (`a\b` becomes `a/b`, a
  different object). Consumers moving an existing bucket behind the router must confirm none of their
  stored keys contain `\`, a `..` **segment** or a leading `/`. `NormalizeAndValidate` rejects a `..`
  *segment* only (`normalized.Split('/').Contains("..")`, `StorageKey.cs`), not the substring, so a file name
  such as `<guid>..webp` passes. That is Themia's reading of the code, not a run. Propertiezy's
  current key generation is clean; its legacy `<guid>..webp` photos (before its fix #220) should pass for the
  reason above, but its staging count is pending and will include non-`public/` keys stored in public
  listing rows. ezy-assets has never held an S3/R2 object (Local only), so it has no bucket to check. Its
  S3 adapter builds the key extension from `Path.GetExtension(fileName)` trimmed only, so a hostile file name
  could yield `<guid>.b\c`, which the router re-spells to the nested `<guid>.b/c` rather than rejecting. Keeping
  a hostile extension out of a key is the key generator's job (ezy-assets will sanitise it), not the
  router's.
- **`Themia.Modules.Storage` is not supported with the router.** Its builder throws if an `IStorageProvider`
  is already registered, and `MapThemiaStorageEndpoints` reads `LocalStorageOptions` from DI
  (`StorageEndpoints.cs:41`). Neither consumer uses it, each verified by its own session
  against its code: propertiezy references `Themia.Storage`, `.AspNetCore` and `.S3` directly; ezy-assets
  references no `Themia.Storage*` package at all.

## 8. Tests (written failing first; each has a falsifier)

1. **Wrong side, adapted from ezy-assets #399** and credited to it: two spy slots. A private write must not
   touch the public spy and a public write must not touch the private spy; reads of `private/x`, a flat key
   `x`, and `private/x/public` must never reach the public spy. #399's inputs are stored paths and URLs
   (`/uploads/private/a.pdf`, `https://cdn/.../public/a.jpg`) and it asserts private-store bytes come back
   for the flat and `public/../x` cases; this router takes keys and normalises first, so the inputs become
   key-form and a normalise-first input (`public/../x`, a leading `/`) is expected to **throw with neither
   spy touched**, not to return bytes. Falsifier: invert the routing and count the failures at implementation
   time; #399's counts (one for inverted writes, eight for inverted reads) do not carry over.
2. **Mismatch throws, and nothing is stored:** `Visibility=Private` + `public/x`, and `Visibility=Public` +
   `x`, each throw `ArgumentException` with both spies untouched. Falsifier: route writes on `Visibility`
   alone and show the object stored and then unreachable by key — the failure this rule exists to prevent.
3. **Normalise first:** `public\x` classifies as public, `/public/x` and `public/../x` are rejected, `Public/x`
   is private. Falsifier: classify the raw key.
4. **Construction refusals:** same instance twice, null slot, public slot with no public container.
   **Factory overload:** a host booted with a half-configured slot (blank Local `SigningKey` with `Validate()` in the
   factory) fails at start, not at first resolve. Falsifier: resolve lazily and show start succeeds.
5. **`PublicOnly`:** constructs with a blank `BucketName`; a private key throws; `PublicOnly=false` with a
   blank `BucketName` still throws (the guard is preserved — this is the test that would fail if the flag
   were replaced by loosening the check); `PublicOnly=true` with a non-blank `BucketName` throws.
6. **URL service over the router:** Local private yields a relative URL joined onto `PresignedBaseUrl`;
   a public-slot key yields the slot's absolute URL untouched. Same `StorageUrlService`, no change to it.
7. **Mount round-trip** (§5.3).
8. The existing single-provider tests are the guard for §7 and are not edited.

## 9. Not covered, and said so

- Moving existing objects between slots. Visibility is immutable once written; a move is an app decision.
- Reading the same key from both slots as a fallback. It would let a private key resolve in the public
  slot, which is exactly what failing closed prevents.
- Config binding in Themia. §6.
