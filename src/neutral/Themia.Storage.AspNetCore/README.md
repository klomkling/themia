# Themia.Storage.AspNetCore

Serves `Themia.Storage`'s **Local** presigned downloads over HTTP — for a host that uses the neutral
`LocalStorageProvider` directly rather than `Themia.Modules.Storage`.

```csharp
// The signer must use the same key as the provider.
builder.Services.AddSingleton<IStorageProvider>(new LocalStorageProvider(localOptions));
builder.Services.AddSingleton(new LocalUrlSigner(localOptions.SigningKey));

// Absolute links, including from a background job with no HTTP request (Themia.Storage).
builder.Services.AddThemiaStorageUrls(o => o.PresignedBaseUrl = "https://api.example.com/api/v1/storage");

app.MapThemiaLocalStorage("/api/v1/storage");     // GET /api/v1/storage/_local/get?key=…&token=…

// anywhere:
var url = await storageUrls.GetDownloadUrlAsync(key, TimeSpan.FromMinutes(15), ct);
```

With an S3-compatible provider, `GetDownloadUrlAsync` returns the provider's signed URL untouched and the
route is never hit.

## What the route does

| request | response |
| --- | --- |
| valid token, object exists | `200`, streamed with its stored content type |
| valid token, no object | `404` |
| missing, tampered or expired token; a token for another key; an upload token | bare `403` |

Every response carries `Cache-Control: private, no-store`, `X-Content-Type-Options: nosniff` and
**`Content-Security-Policy: sandbox`**.

**The sandbox is the header that matters.** `nosniff` stops a browser *guessing* a type; it does nothing
for a file whose declared type is already dangerous. An uploaded SVG containing a `<script>`, stored as
`image/svg+xml` and opened from its presigned link, would otherwise run **on your API's origin** — the same
for `text/html`. Sandboxed, the document gets a unique origin and no script; images and PDFs render as
usual.

## Anonymous by construction

The token is the credential, exactly as an S3 presigned URL's signature is, and the link is opened by a
browser or mail client that carries no session. So the route is mapped in a group that is never handed
back (a host's `RequireAuthorization()` cannot reach it) **and** carries `AllowAnonymous` — because an
authorization `FallbackPolicy`, a common hardening, applies to every endpoint *without* auth metadata and
would otherwise put every link behind a login it cannot satisfy.

## Fails at startup, not in production

- No `LocalUrlSigner` registered → `MapThemiaLocalStorage` throws. Otherwise every download fails.
- `PresignedBaseUrl` set but not ending with the mount you map → throws. An absolute URL for the wrong
  mount passes "is it absolute?" and 404s on every link.
- `PresignedBaseUrl` not an absolute `http(s)` URL, or carrying a query → the host fails to start.
- A Local provider with no `PresignedBaseUrl` → `GetDownloadUrlAsync` throws naming the option, rather
  than returning a relative link that goes into an email and breaks there.

The base URL is joined as a string on purpose. `new Uri(new Uri("https://host/api/v1/storage"), "_local/get")`
resolves RFC-3986-style and **drops the last segment** — `https://host/api/v1/_local/get` — so a missing
trailing slash is the whole difference between working links and a 404 on every one. Both forms are tested.

## Keep the token out of your logs

The token travels in the query string and is a bearer credential for the life of the URL. This package
cannot control your host's request logging; if it writes query strings, exclude this route.

## Not included

Presigned **uploads** (`_local/put`) — still served by `Themia.Modules.Storage` only — range requests,
and ETags.
