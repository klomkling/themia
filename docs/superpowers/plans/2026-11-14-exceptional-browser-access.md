# Plan: Exceptional dashboard browser access (coord #0216)

Risk: high (auth). No code before lead-plan-reviewer GO. Package: `Themia.Exceptional.AspNetCore` (net8;net10).

## Problem
Alert links open `/admin/exceptions/<guid>` in a browser. Apps authenticate with a Bearer header only, so `Authorize`
denies and the dashboard answers its bare 404. A browser cannot send the header.

## What exists (verified in source)
- `ExceptionalDashboardOptions.Authorize` (`Func<HttpContext,Task<bool>>`, fail-closed) runs first on every route; `OnDenied` is an opt-in hook.
- Action POSTs (`protect/delete/hard-delete`) already use a double-submit CSRF cookie (`__themia_csrf`, Path=/, Strict) plus same-origin check.
- `Themia.Challenges` already issues hashed, single-use, TTL'd `OpaqueToken` secrets bound to a `ChallengeScope(Key, Purpose, TenantId)` and resolves them with `VerifyByTokenAsync` (rate-limited). No ticket/cookie-session code exists in Exceptional.

## Design
Opt-in: `options.BrowserAccess = new(...)`. Unset = today's behaviour exactly.
1. **Mint** `POST {prefix}/ticket`. App callback `MintUser(ctx) -> string? userId` (app does Bearer + permission, e.g. `system:exceptions:read`). Themia issues a Challenges token: Exceptional configures its own purpose `exceptional-dashboard` (`OpaqueToken(32)`, TTL 60s, wider issue window, `MaxLive > 1`, so the 3/15min, 1-live defaults do not bite), scope key = `exceptional-dashboard:` + userId (the 20/h key bucket is shared across purposes, so the prefix keeps it separate). `RateLimited` maps to 429. Returns `{ redeemUrl }`, a RELATIVE URL (never built from `Request.Scheme/Host`, which is http behind the proxy). Mint never accepts the dashboard cookie, so a cookie cannot mint more cookies.
2. **Redeem** `GET {prefix}/session?ticket=&return=`. `VerifyByTokenAsync` consumes it atomically (single use; it is NOT rate-limited, and we do not enable the app-wide `TokenVerifyWindow`: 256-bit random + 60s TTL is the bound), then app callback `AuthorizeUser(ctx, userId) -> bool` re-checks active user + permission. Any failure: the same bare 404 as today (no oracle). Success: set cookie, 303 to a validated return path, response `Referrer-Policy: no-referrer`, `Cache-Control: no-store`.
3. **Cookie** `__themia_dash`: HttpOnly, Secure, SameSite from option `SameSite` (default Strict; Lax when web and API are cross-site, startup does not guess), `Path={PathBase}{prefix}`, absolute 15 min. SameSite: Strict is used only if the prod web and API hosts are same-site (a cross-site 303 would drop it); host pairs to be confirmed with lead, else Lax (no sliding). Value = `IDataProtector` payload with embedded expiry (userId, expiry), purpose-bound, stateless, so multi-node safe.
4. **Authorize**: Bearer first (unchanged). Else, if cookie decrypts and is unexpired, call `AuthorizeUser(ctx, userId)` on every request, so deactivation or permission loss takes effect immediately, not after 15 min. The cookie does not set `ctx.User`; its Path keeps it off other routes.
5. **Return path**: must start with `{prefix}/`, reject `//`, `\`, `%2f`/`%5c` tricks, schemes, control chars, over-length; else fall back to `{prefix}`.
6. **Ticket never logged**: add `ticket` to Exceptional's `SecretKey` list (a custom Redactor bypasses the scrub, so also strip it in the capture path) and wrap the whole redeem handler in catch-all so no exception capture carries the query, the redeem 303 drops it from the final URL, document that app request logging must not record the query on this route. Never log it ourselves.
7. **Alert link base**: dropped, `ExceptionAlertOptions.AdminUrlTemplate` already exists; apps point it at the web host.

## Tests (TestServer with a fake in-memory single-use `IChallengeService` and mutable clock; real-engine ticket semantics rely on Themia.Challenges' own tests)
Expired, reused (second redeem 404), wrong purpose, ticket for user A never yields B, permission revoked between mint and redeem, user deactivated, permission revoked while cookie live (next request denied), return outside prefix (`//evil`, `/\evil`, encoded, absolute URL), cookie attributes and Path scope, tampered/expired/other-purpose cookie, cookie cannot mint, CSRF still required on action POSTs with cookie auth, ticket absent from captured exceptions and from our logs (capturing logger), BrowserAccess unset = no new routes, fail-closed when callbacks are null.

## Review decisions (GO WITH CHANGES, 08:34Z, all folded above)
- A. Reuse `Themia.Challenges`: reviewer agreed. Startup fails when `BrowserAccess` is set and `IChallengeService` is not registered. opsezy has no Challenges: its migration goes in the opsezy row.
- B. Redeem as GET: reviewer OK with the masking conditions above.
- C. Per-request `AuthorizeUser`: reviewer agreed.

## Release
Additive, minor. PublicAPI, README, CHANGELOG. One PR for Themia; one coord row per app afterwards (web page + `AlertLinkBase`).
