# Themia.Conversations.AspNetCore

Minimal-API endpoints over `Themia.Conversations`: guest link exchange, list, post (JSON or multipart with files), read mark and attachment download. The host supplies identity; the package never decides who a caller is.

```csharp
services.AddThemiaConversations(...);
services.AddThemiaConversationsHttp(o => o.ClientKey = ctx => ctx.Request.Headers["CF-Connecting-IP"].ToString());
services.AddScoped<IConversationActorResolver, MyResolver>();   // required: startup fails without it

app.MapThemiaConversations("/api/v1");
```

## Routes (under the prefix)

| Method and path | Body | Result |
|---|---|---|
| `POST /guest/exchange` (only when `IGuestAccess` is registered) | JSON `{tenantId, token}` | `{conversationId, expiresAt}` and the guest cookie |
| `GET /conversations/{id}/messages?afterSeq=&limit=` | none | message page |
| `POST /conversations/{id}/messages` | JSON `{clientMessageId, body}` or `multipart/form-data` (`clientMessageId`, `body`, `files`*) | `201 {messageId, seq}`; a duplicate is `200` |
| `POST /conversations/{id}/read` | JSON `{seq}` | read mark |
| `GET /conversations/{id}/attachments/{attachmentId}` | none | `{url, fileName, contentType, size}`; the URL is short-lived and presigned |

Files need `IConversationAttachments` (`AddThemiaConversationAttachments()`); without it a multipart post is `415`. Every response is `Cache-Control: no-store`.

## Who the caller is

1. Your `IConversationActorResolver` runs first. Return `null` for "not a member".
2. If it returned `null`, the guest cookie for the **route's** conversation id is used.
3. Neither: `401`. The service refusing (wrong conversation, tenant, expired or revoked key, not a participant): `404` with one identical body, so nothing tells a caller which part was wrong.

## Guest cookie

`__Host-themia_conv_<conversationId:N>`: `HttpOnly`, `Secure`, `SameSite=Strict`, `Path=/`, no `Domain`, expiring with the session. One cookie per conversation, so a guest in two rooms keeps both. The value is unsigned (`{tenant, key}`); the key is 256 random bits and the store re-proves it, with tenant and conversation, on every call, so a tampered or swapped value only ever reaches a `404`. The key never appears in a URL, a response body, a log line or a problem document.

- **The web app and the API must be same-site** (`SameSite=Strict`). If they are not, put the API behind the web origin's reverse proxy. CORS with credentials only for that one origin.
- Put the invite token in the URL **fragment** (`https://app/chat#t=...`) and POST it from script. A fragment is not sent to servers, logged by proxies or put in `Referer`.

## CSRF

Every `POST` needs `X-Requested-With: themia` (`403` otherwise), checked before anything else, including before the body is read. A cross-site form cannot set it, and a cross-origin script needs CORS to.

## Order inside a request

CSRF header, then caller, then rate limit, then body size, then body parse, then validation, then the service. Each step can refuse without the next running; tests count the body reads to prove it.

## Limits

| Option | Default | Scope |
|---|---|---|
| `ExchangesPerHour` | 20 | per client, before the body is read |
| `UnprovenGuestRequestsPerMinute` | 60 | per client, for cookie requests the store has not accepted yet; the slot is refunded once it accepts |
| `PostsPerMinute` | 30 | members per tenant, conversation and user; guests per client |
| `MaxJsonBodyBytes` | 64 KiB | JSON routes; multipart is capped at `MaxAttachmentBytesPerMessage` plus this |

Over a limit: `429` with `Retry-After`.

**`ClientKey`** names the caller for the per-client limits. The default is the connection's remote address (IPv6 as its /64, IPv4-mapped unwrapped), and a startup warning says so, because **behind a proxy that is the proxy's address and every caller shares one bucket**. Set it from the header your edge guarantees. Return `null` and all such callers share one bucket.

**Known limits of the limiter:**

- It is **per node and in memory**; counts reset on restart and are not shared across instances. A multi-node deployment gets limit x nodes. Put an edge limiter in front for a hard cap.
- Each scope holds at most 10,000 buckets. A flood of distinct keys can reset buckets that are under their limit, and keys it cannot track share an overflow bucket (limit x 10), so a flood can throttle new keys together. Buckets already at their limit are never evicted, so they stay blocked until their window ends.

## Validation (`422`, stable `code`)

`clientMessageId` 1 to 64 characters with no whitespace, `afterSeq >= 0`, `1 <= limit <= MaxListLimit`, `seq >= 0`, at most `MaxAttachmentsPerMessage` files, no unknown JSON members or form fields. Outcomes: closed `409`, rejected attachment `422`, quota `413`.

## Not here

Open, join, invite and close stay in the app: who may start or end a conversation is app policy. There is no staff redact route.
