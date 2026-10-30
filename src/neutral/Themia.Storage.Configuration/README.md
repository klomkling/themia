# Themia.Storage.Configuration

One configuration shape for storage, shared by every app. `AddThemiaStorageFromConfiguration` reads the `Storage`
section, registers the app's `IStorageProvider` (and `IStorageUrlService`), and stops the host at start when the
configuration is wrong.

```csharp
builder.Services.AddThemiaStorageFromConfiguration(builder.Configuration);
```

**Private files are always on Local.** Every key outside `public/` goes to the Local provider, and nothing in the
configuration can change that: `Storage:Private:Provider` accepts `Local` or nothing, and `Storage:Provider=S3` is refused.
The public slot (`public/...` keys) is Local, or S3/R2 with `Storage:Public:Provider`. With S3/R2 the provider is a
`SplitStorageProvider` whose S3 side is public-only and has no private bucket.

The package references `Themia.Storage.S3`, so a Local-only app restores `AWSSDK.S3` too.

## Keys

A relative path is under the content root. In an environment variable `:` is `__`.

| Key | Environment variable | Needed | Meaning |
|---|---|---|---|
| `Storage:Public:Provider` | `Storage__Public__Provider` | no | `Local` (default), `S3` or `R2` |
| `Storage:Private:Provider` | `Storage__Private__Provider` | no | blank or `Local`; anything else stops the host |
| `Storage:Local:RootPath` | `Storage__Local__RootPath` | yes | The private store |
| `Storage:Local:SigningKey` | `Storage__Local__SigningKey` | yes | Signs Local presigned URLs |
| `Storage:Local:PresignedBaseUrl` | `Storage__Local__PresignedBaseUrl` | for Local presigned URLs | Absolute URL of the `MapThemiaLocalStorage` mount |
| `Storage:Local:PublicBaseUrl` | `Storage__Local__PublicBaseUrl` | no | Local public slot only: turns the public container on |
| `Storage:Local:PublicRootPath` | `Storage__Local__PublicRootPath` | no | Local public slot only; default `<RootPath>/public` |
| `Storage:S3:PublicBucketName` | `Storage__S3__PublicBucketName` | S3/R2 | The public bucket |
| `Storage:S3:PublicBaseUrl` | `Storage__S3__PublicBaseUrl` | S3/R2 | Absolute URL public objects are served from |
| `Storage:S3:AccessKey`, `Storage:S3:SecretKey` | `Storage__S3__AccessKey`, `Storage__S3__SecretKey` | S3/R2 | Both required; the AWS ambient credential chain is not used |
| `Storage:S3:ServiceUrl` | `Storage__S3__ServiceUrl` | R2/MinIO | Endpoint |
| `Storage:S3:Region` | `Storage__S3__Region` | no | e.g. `auto` for R2 |
| `Storage:S3:ForcePathStyle` | `Storage__S3__ForcePathStyle` | no | Default `true` |

Refused at start: an unknown key under `Storage:S3`, `Storage:Local`, `Storage:Public` or `Storage:Private`; `Storage:S3:*`
with a Local public slot; `Storage:Local:PublicRootPath`/`PublicBaseUrl` with an S3/R2 public slot; a public root inside the
private root's `blobs`/`content-types` directory (or the reverse). Messages name the key and never a value. The root checks compare paths, not symlinks or junctions: a link from one root into the other is not caught.

## Deprecated keys (read with a startup warning)

Removed in the first release after every adopting app has switched; the CHANGELOG announces it.

| Old | New |
|---|---|
| `Storage:Provider` = `Local` / `Split` | `Storage:Public:Provider` = `Local` / `S3` (`S3`, `R2` or any other old value is refused) |
| `Storage:Local:UploadsDirectory` | `Storage:Local:RootPath` |
| `Storage:DownloadBaseUrl` | `Storage:Local:PresignedBaseUrl` |

Setting an old key together with its new one is refused (`DownloadBaseUrl` may repeat the same value).
**`Storage:S3:BucketName` is not accepted**: it is now `Storage:S3:PublicBucketName`, and there is no private bucket.
`RootPath` has no default; an app that relied on the `uploads` default sets `Storage:Local:RootPath=uploads`.
`SigningKey` is required even with a Local-only public slot.

## Per app: old key -> new key

Copy the app's block into its adoption runbook. Names are environment variables.

**Propertiezy** (today `Split`)

| Old | New |
|---|---|
| `Storage__Provider=Split` | `Storage__Public__Provider=R2` |
| `Storage__S3__*` (`ServiceUrl`, `ForcePathStyle`, `Region`, `PublicBucketName`, `PublicBaseUrl`, `AccessKey`, `SecretKey`) | unchanged |
| `Storage__Local__RootPath`, `SigningKey`, `PresignedBaseUrl` | unchanged |

**EzyAssets**

| Old | New |
|---|---|
| `Storage__Public__Provider`, `Storage__Private__Provider=Local` | unchanged |
| `Storage__S3__BucketName` (the public bucket) | `Storage__S3__PublicBucketName` (the old name is refused) |
| `Storage__S3__PublicBaseUrl`, `ServiceUrl`, `Region`, `AccessKey`, `SecretKey`, `ForcePathStyle` | unchanged (`AccessKey` and `SecretKey` both required) |
| `Storage__Local__UploadsDirectory` (default `uploads`) | `Storage__Local__RootPath=uploads` (no default; set it explicitly) |
| `Storage__Local__PublicBaseUrl` | unchanged (Local public slot only) |
| (none) | `Storage__Local__SigningKey`: new and required. Generate a random value of 32+ characters in the secret store (Coolify); never a file |

**EzyServices** (Local only)

| Old | New |
|---|---|
| `Storage__Provider=Local` | remove (Local is the default) |
| `Storage__Local__RootPath`, `SigningKey` | unchanged |
| `Storage__DownloadBaseUrl` | `Storage__Local__PresignedBaseUrl` |

## CDN purge and the Local endpoints

**Purge.** With an S3/R2 public slot, the binding wraps that slot with `PurgingStorageProvider` when an `ICdnPurger` is
registered (`AddThemiaStorageCloudflarePurge`, in any order relative to this call). Deleting a `public/` key then purges
its public URL; private keys are never purged. **Do not wrap the public slot yourself** and do not register your own
`IStorageProvider` next to this binding: the host stops at start. A Local public slot is not wrapped (the resolved
provider stays a `LocalStorageProvider`, which apps cast to map the public mount).

**Signer.** The binding registers the `LocalUrlSigner` that `MapThemiaLocalStorage` verifies with, built from
`Storage:Local:SigningKey` (`TryAdd`: a signer you register yourself is kept). Drop your own registration once you are on
the binding; a signer built from a different key makes every presigned link 403.
