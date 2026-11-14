# Exceptional dashboard: browser access (coord #0216)

Alert links open `{prefix}/{id}` in a browser, which cannot send a Bearer header. `BrowserAccess` (opt-in) lets an
app trade its Bearer-authenticated session for a short-lived dashboard cookie. Unset = no new routes, no behaviour change.

## Flow

1. The app's web page calls `POST {prefix}/ticket` with the user's Bearer token. `MintUser(ctx)` authenticates and
   returns the user id (or null: bare 404). Themia returns `{ "data": { "redeemUrl": "/admin/exceptions/session?ticket=..." } }`
   (relative; `PathBase` + prefix, never built from the request host). The ticket is a single-use, 60 s
   `Themia.Challenges` `OpaqueToken`.
2. The page navigates the browser to `redeemUrl` (optionally `&return=/admin/exceptions/{id}`). `AuthorizeUser(ctx, userId)`
   re-checks the user, then a cookie `__Secure-themia_dash` is set (HttpOnly, Secure, `Path={PathBase}{prefix}`, absolute
   `CookieLifetime`, default 15 min, no sliding) and the browser is redirected (303) to the validated return path.
3. Every dashboard request accepted by the cookie re-runs `AuthorizeUser`, so deactivation or permission loss applies at
   once. The cookie never authenticates `ticket` (a cookie cannot mint cookies) and does not set `ctx.User`. Action POSTs still need the CSRF token.

Every failure is the same bare 404. The cookie payload is protected with ASP.NET Core Data Protection (stateless; persist
keys across nodes).

## Setup

```csharp
services.AddThemiaChallenges(o => o.AddExceptionalDashboardPurpose());   // required; startup fails otherwise
endpoints.MapThemiaExceptional("/admin/exceptions", o =>
{
    o.Authorize = ...;                                    // unchanged Bearer check
    o.BrowserAccess = new ExceptionalBrowserAccessOptions
    {
        MintUser = async ctx => /* Bearer + permission -> user id, or null */,
        AuthorizeUser = async (ctx, userId) => /* user still active and permitted */,
    };
});
```

Point `ExceptionAlertOptions.AdminUrlTemplate` at the **web** host that serves the page performing steps 1-2.

## Operational rules

- **SameSite.** Default `Strict`. If the web page and the API are on different registrable domains, the 303 is
  cross-site and Strict drops the cookie: set `SameSite = SameSiteMode.Lax`. Startup does not guess.
- **Logging.** The ticket is in the redeem URL's query. App request logging, reverse-proxy access logs and APM must not
  record the query on `{prefix}/session`. Themia adds `ticket` to the exception redactor and logs only exception types.
- `MintUser` must authenticate with the Bearer token (never a cookie).

## Known residual: ticket handed to another browser

The redeem GET is not bound to the browser that minted the ticket. Someone holding their own valid ticket could lure a
victim's browser to their redeem URL, leaving the victim with the attacker's dashboard session (the attacker gains no
access they lack; the victim's dashboard actions would run as the attacker). Bounded by the 60 s single-use ticket and the
15 min cookie. A `Sec-Fetch-Site` check cannot close it without breaking the cross-site (Lax) deployment, so it is left for
api-review to decide.
