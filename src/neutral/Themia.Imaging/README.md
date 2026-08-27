# Themia.Imaging

Normalize an uploaded image for storage: **downscale, bake in EXIF orientation, drop metadata,
re-encode** — with the **decompression-bomb guard** a hand-rolled processor omits.

Neutral core. Pure computation: no HTTP, no clock, no credentials, no database. `net8.0;net10.0`.

Ported from ezy-assets' production implementation (coord #0101) rather than redesigned.

## Install

```xml
<PackageReference Include="Themia.Imaging" Version="..." />

<!-- The native codec for the RID YOU run on. See "Native assets" below. -->
<PackageReference Include="SkiaSharp.NativeAssets.Linux" Version="4.151.1" />
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

**This package references managed `SkiaSharp` only.** The native codec is a **host** decision, keyed
to the RID the host actually runs on:

| where | add |
| --- | --- |
| Linux container | `SkiaSharp.NativeAssets.Linux` |
| developer Mac | `SkiaSharp.NativeAssets.macos` |
| Windows | nothing — `SkiaSharp` carries the Windows binaries |

Shipping one RID's binaries from a neutral package would force them on every consumer. The failure
mode this avoids is the expensive one: works on a developer's Mac, fails in the container.

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
