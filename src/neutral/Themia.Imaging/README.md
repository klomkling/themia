# Themia.Imaging

Normalize an uploaded image for storage: **downscale, bake in EXIF orientation, drop metadata,
re-encode** — with the **decompression-bomb guard** a hand-rolled processor omits.

Neutral core. Pure computation: no HTTP, no clock, no credentials, no database. `net8.0;net10.0`.

Ported from ezy-assets' production implementation (coord #0101) rather than redesigned.

## Install

```xml
<PackageReference Include="Themia.Imaging" Version="..." />

```

```csharp
builder.Services.AddThemiaImaging(o =>
{
    o.MaxEdge = 1600;                    // longest edge; never upscales
    o.Quality = 80;
    o.Format = ImageOutputFormat.Webp;
    o.MaxPixels = 100_000_000;           // the decompression-bomb budget
});
```

Bad options fail the host at startup and name the value, rather than surfacing on somebody's first
upload.

## Use

```csharp
// after your own content-type allowlist and byte limit — both HTTP concerns, and yours
using var processed = await imageProcessor.ProcessAsync(uploadStream, ct: ct);

var key = $"listings/{listingId}/{Guid.CreateVersion7()}{processed.Extension}";
await storage.PutAsync(key, processed.Content,
    new StoragePutOptions(Visibility: StorageVisibility.Public), ct);
```

`ProcessedImage` is `(Stream Content, string Extension, int Width, int Height)` and implements
`IDisposable` — **disposing it disposes the stream**, so keep it alive until the write completes.

Extension rather than content-type, because storage names the object by extension and derives the
served content-type from it; carrying both invites the two to disagree. Dimensions are there so a
caller that persists them does not have to decode the result again.

Options can also be passed per call, for a consumer that wants one size for a listing photo and
another for an avatar:

```csharp
using var avatar = await imageProcessor.ProcessAsync(stream, new ImageProcessingOptions { MaxEdge = 256 }, ct);
```

A file that is not a decodable image, or one over the pixel budget, throws `ArgumentException` — a
condition to report to whoever uploaded it, not a fault to page someone about.

## The decompression-bomb guard

**This is the part a reimplementation skips, and it is why the package exists.**

An upload byte-limit bounds the *encoded* size and says nothing about the decoded one. This package's
own test fixture is a **48-byte** PNG that declares 12000×12000 and decodes to **144 MB**; at
30000×30000 it is still trivial on the wire and decodes to about **900 MB**. An endpoint with a 10 MB
limit accepts it, looks fully guarded, and OOMs the box — and on a shared host, one product's OOM is
its neighbour's outage.

So the pixel count is read from the codec's header and checked **before anything is decoded**:

```csharp
var info = codec.Info;                                  // dimensions, no pixels
if (ExceedsPixelBudget(info.Width, info.Height, max))   // long arithmetic: 60000² overflows int
    // (internal — the guard is not a knob, MaxPixels is)
    throw new ArgumentException($"Image dimensions {info.Width}x{info.Height} exceed …");
```

A test asserting "a 12000×12000 PNG is rejected" passes whether the guard sits before or after the
decode. The one in this package asserts it is rejected **without the process growing**, which is the
only version that pins the guard's position — and it is the single test that fails when the check is
moved after the decode.

Above the budget the image is refused outright rather than downscaled: a caller cannot tell a mistake
from an attack, and silently accepting a 900 MP upload is the behaviour the guard exists to prevent.

Below it, a **JPEG or WebP** is never materialized at full resolution — the decode happens at the
largest power-of-two subsample whose long edge still clears `MaxEdge`, and the downscale trims from
there. Quality is unaffected: the decode is always at least the target size before the final resize.

**PNG is not subsampled, because Skia cannot** — `GetScaledDimensions` returns the full size for any
scale, which this package's tests pin so the claim cannot drift back. For PNG the budget is the only
bound, and at the default 100 MP that is a **~400 MB decode**. If your uploads accept PNG and your
host is memory-constrained, set `MaxPixels` from the memory you are willing to spend rather than from
the camera you expect.

### `MaxPixels` bounds one decode. `MaxConcurrency` is the other term

A pixel budget is not a memory budget. `MaxPixels` bounds a single call, and nothing about it counts
how many calls are in flight, so the ceiling a *process* can actually reach is:

```
MaxPixels × 4 bytes × MaxConcurrency
```

At the defaults (100 MP, 2) that is about **800 MB**. Supply only the first two terms and the third
one is set by whatever traffic arrives: a browser doing `Promise.allSettled(files.map(upload))` on
eight selected photos is eight simultaneous requests, so at a configured 64 MP that is ~2 GB of
decode buffers for one click. This is sharpest for PNG, which cannot be subsampled — every concurrent
decode is a full-budget allocation from a file that may be a few hundred bytes on the wire.

`MaxConcurrency` (default **2**) is a `SemaphoreSlim` taken *after* the budget check, so an oversized
image is refused without occupying a slot, and released in a `finally`, so a throw — the common case
on an upload endpoint, not the rare one — hands the slot straight back. Callers beyond the bound queue
and honour their cancellation token, so a client that disconnects while queued frees its place.

```csharp
services.AddThemiaImaging(o =>
{
    o.MaxPixels = 64_000_000;   // one decode
    o.MaxConcurrency = 2;       // × 4 bytes × this = ~512 MB, the process ceiling
});
```

**It bounds one process, not the machine.** A semaphore knows nothing about other instances, so behind
a load balancer the real ceiling is `instances × MaxConcurrency × MaxPixels × 4 bytes`. If the goal is
"never let this starve a neighbouring process", set a container memory limit too — that is the
operator's job and no in-process bound can do it.

## Orientation and metadata are opposite operations on the same field

Re-encoding from a decoded pixel buffer is what drops EXIF — including GPS coordinates, which on a
property listing are the coordinates of the property and often of the seller's own home. Publishing
them is a privacy incident that looks exactly like a working feature.

But the orientation tag has to be **read and applied to the pixels** before the metadata goes, or
every portrait phone photo publishes sideways. Shipping the strip without the honour is easy and
looks fine on a laptop screenshot.

Both are tested end to end, over all eight EXIF origins, against JPEGs whose EXIF segment this
package's test suite writes by hand — SkiaSharp's encoder emits no EXIF, so a fixture round-tripped
through it can never carry any, and a suite built on one proves nothing about either property.

## Native assets

**This package ships the Linux native codec, and nothing else RID-specific.** Nothing to add on Linux
or Windows; on a developer Mac, SkiaSharp supplies the macOS binaries itself.

| where | add |
| --- | --- |
| Linux container | nothing — shipped with this package |
| developer Mac | nothing — `SkiaSharp` carries the macOS binaries |
| Windows | nothing — `SkiaSharp` carries the Windows binaries |

**This reverses what versions up to 0.22.0 said**, and the reversal is worth understanding before you
copy the old advice from somewhere. The original principle — a native codec is a host decision, so a
neutral package should not force one RID's binaries on everyone — was already lost upstream: SkiaSharp
declares `NativeAssets.macOS` and `.Win32` transitively for every modern target framework, roughly 83 MB
a consumer receives without asking. Linux is the single RID it leaves opt-in.

So the package was neutral about exactly one platform, and it was the platform every deployment runs.
That produced the expensive failure precisely: a developer Mac worked, the package looked self-contained,
and the container threw at the first decode (coord #0110). Shipping Linux too costs 55.7 MB on hosts that
never run it and removes the footgun entirely.

## Why SkiaSharp and not ImageSharp

Recorded because it will otherwise be rediscovered by whoever picks an imaging library next, and
ezy-assets has already paid for it once:

> **SixLabors.ImageSharp v4 requires a paid licence for commercial use and fails the build.**

It is the obvious first choice and the wrong one for a commercial product. **SkiaSharp 4.151.1 is
MIT** — verified on the *published* nuspec, not on the repository's LICENSE file, because those can
diverge: ImageSharp kept an Apache-2.0 repo while its NuGet package went commercial.

## Not included

Storage (`Themia.Storage`), content-type allowlisting and byte limits (an HTTP concern), virus
scanning, thumbnails and variants, CDN, and anything needing a request context.
