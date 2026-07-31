# Themia.Messaging — HMAC transport (coord #0050, step 3)

**Date:** 2026-07-31
**Status:** draft, pending review
**Target version:** unreleased
**Request:** coord #0050, from `propertiezy`

## Why

Steps 1, 2 and 2b (merged as `9acfd35`) give adopters durable, deduplicated, multi-engine message
*storage*. They do not give them a way to send a message to a peer: the drainer claims a row and hands
it to an `IOutboxDispatcher<ClaimedMessageRow>`, and no HTTP implementation of that interface exists.
Until one does, every adopter writes their own signing and delivery code — which is most of what #0050
asked us to stop them writing.

This spec covers the transport: signing, sending, verifying, and the loop guard.

## The scheme — `themia-hmac-v1`

Canonical string, exactly as pinned by the golden vector fixture propertiezy supplied:

```
{timestamp}\n{METHOD}\n{pathAndQuery}\n{body}
```

| Element | Rule |
|---|---|
| `timestamp` | ISO-8601 round-trip (`"O"`), 7 fractional digits, trailing `Z`. The same string appears in the header and in the canonical string — one value feeds both. |
| `METHOD` | Upper-invariant. |
| `pathAndQuery` | Exactly as sent: leading `?`, raw query, not re-encoded, not decoded, not reordered. |
| `body` | The raw body string. Empty body is the empty string, and its separator newline is **retained** — the segment is never omitted. |
| separator | `\n` (LF) only. Never `\r\n`. |
| signature | `HMACSHA256` over `UTF8.GetBytes(canonical)`, keyed with `UTF8.GetBytes(secret)` — the secret is raw UTF-8 string bytes, never hex- or base64-decoded. Rendered **lowercase hex**. |

This is not negotiable and not configurable. Canonicalization is where signature-bypass bugs live, and
an adopter-swappable canonical string would reintroduce exactly the protocol drift #0050 exists to
prevent.

## Packages

| Package | Holds | Depends on |
|---|---|---|
| `Themia.Messaging.Hmac` | canonicalizer, signer, verifier, peer/key registry, committed vectors | — |
| `Themia.Messaging.Http` | `HttpMessageDispatcher : IOutboxDispatcher<ClaimedMessageRow>` | `.Hmac`, `Themia.Messaging` |
| `Themia.Messaging.AspNetCore` | verify filter, loop guard, `EnableBuffering` | `.Hmac` |

All `net10.0`, consistent with the TFM reversal recorded in #0050.

The split means a receive-only service never pulls `IHttpClientFactory`, and a send-only worker never
pulls ASP.NET. The scheme package has no HTTP or ASP.NET dependency at all, so it is testable in
isolation and reusable by a future non-HTTP transport.

## Headers

Signed content is the canonical string only. Everything below rides as an **unsigned selector header**:

| Header | Purpose |
|---|---|
| `{prefix}Timestamp` | The signed timestamp, byte-identical to the canonical string's first segment. |
| `{prefix}Signature` | Lowercase hex. |
| `{prefix}Key-Id` | Selects which key verifies. |
| `{prefix}Scheme` | e.g. `themia-hmac-v1`. |
| `{prefix}Origin` | The originating system, for the loop guard. |

`{prefix}` defaults to `X-Themia-` and is **configurable per peer**.

That configurability is deliberate and is *not* a hole in the "no knobs on the wire format" rule. Header
names are not signed, so a mismatch can only cause a failure to verify, never a bypass — the failure
direction is safe. It exists because the live ezy-assets ↔ propertiezy link currently sends
`X-Propertiezy-Timestamp`: with the framework's default prefix the receiver would look for a header
that is not there, fail before ever computing a signature, and return 401 with a perfectly correct
signature sitting in a header it did not read. The canonical string being byte-identical does not save
that cutover.

An unsigned `Key-Id` is not a weakness: it only selects which key to verify against, and an attacker who
changes it merely causes verification to fail, since they still cannot forge a signature under the other
key.

## Keys and rotation

Per-peer and per-**direction** secrets, following the house rule set in #0023 — ezy-assets deliberately
used a second secret S2 distinct from the ingest secret S1 so that compromising one direction does not
confer the other.

- **Outbound:** exactly one key per peer, identified by its `kid`, used to sign what we send.
- **Inbound:** a **set** of keys per peer, keyed by `kid`, any of which may verify what we receive.

The inbound set is what makes rotation possible without a synchronized restart on both sides: publish
the new key as accepted, cut senders over, then retire the old one.

```csharp
services.AddThemiaMessagingHmac(o =>
{
    o.AddPeer("propertiezy", p =>
    {
        p.BaseAddress = new Uri("https://sell.propertiezy.co.th");
        p.HeaderPrefix = "X-Propertiezy-";              // legacy link; omit for the default
        p.SignWith(keyId: "s1-2026", secret: cfg["Hmac:S1"]!);
        p.Accept(keyId: "s3-2026", secret: cfg["Hmac:S3"]!);
        p.Route("listing.snapshot.v1", "/api/v1/ingest/listings");
        p.Route("lead.created.v1", "/api/v1/leads");
    });
});
```

## Sending — `Themia.Messaging.Http`

`HttpMessageDispatcher` implements `IOutboxDispatcher<ClaimedMessageRow>`. The drainer already owns
claiming, leasing, backoff and dead-lettering, so the dispatcher only delivers and classifies.

Per claimed row: resolve the peer from `Destination`, resolve the path from that peer's **type → path**
map, send `Payload` **verbatim** as the body, sign with the peer's outbound key, and attach the headers
above plus any `Headers` the envelope carried.

Sending the payload verbatim is load-bearing. Re-serializing it would change the exact bytes the
signature covers; raw-body signing is only sound because nobody re-encodes it in between.

**No retry, no circuit breaker, no Polly.** The outbox owns retry and backoff. A second retry layer
would multiply attempts and make `MaxAttempts` meaningless. Timeout is per-peer.

### Response classification

| Response | Outcome |
|---|---|
| 2xx | `Delivered` |
| 401, 403, 400, 404, 422 | `Permanent` — dead-letter now |
| **408** (incl. stale timestamp), 425, 429 | `Transient` |
| 5xx, timeout, socket fault | `Transient` |
| Unroutable `Type` (no path configured) | `Permanent` |

A rejected signature dead-letters on the first attempt so a misconfiguration is visible at once rather
than five attempts later. Dead rows are retained 90 days, so recovery is a deliberate replay.

**`Retry-After` is NOT honoured in v1.** A `429` or `503` carrying `Retry-After` is retried on the
outbox's own backoff schedule — roughly two seconds after the first failure — not on the schedule the
peer asked for. This is a real limitation, stated rather than implied: `DispatchResult` is
`(Outcome, Error, Exception)` and carries no retry-delay, and `OutboxDrainer` computes the next attempt
unconditionally from `BackoffPolicy.NextAttemptAt(now, attempts)`, so no dispatcher can influence it.
Honouring it means adding `TimeSpan? RetryAfter` to `DispatchResult` and teaching the drainer to prefer
it — a change to an already-merged package, deliberately deferred to its own request rather than
smuggled in here. Until then the framework ignores explicit peer backpressure, which matters most
against a peer that is rate-limiting precisely because it is already struggling.

## Receiving — `Themia.Messaging.AspNetCore`

An endpoint filter, in strict order:

1. Reject a body larger than `MaxBodyBytes` with **413**, before reading or hashing anything.
2. `EnableBuffering(bufferThreshold, bufferLimit)`, read the body as a string.
3. Unknown or missing scheme version → **400**. This is a protocol mismatch, not a bad credential.
4. Timestamp missing or unparseable → **401**. Timestamp outside the freshness window → **408**
   (see *Freshness window* below — this status is load-bearing, not cosmetic).
5. Unknown `kid` for this peer → **401**.
6. Recompute the canonical string and compare with `CryptographicOperations.FixedTimeEquals`.
7. **Then** the loop guard.

### Body size limit

Raw-body signing forces reading the body twice, so buffering is unavoidable — but the body must be read
*before* the signature can be computed, which puts the allocation on the unauthenticated path. Steps 1
and 2 are reachable by anyone who can address the endpoint; step 6 is the first point at which a caller
has proven anything. An unbounded buffer there is a memory-exhaustion vector that needs no valid key.

`MaxBodyBytes` therefore bounds the read and defaults to a low-megabyte cap. Payloads here are JSON
snapshots, so the cap costs legitimate traffic nothing, and `EnableBuffering` is given an explicit
`bufferLimit` so an oversized body fails at the stream rather than after materialising.

The loop guard runs last, after verification, because `Origin` is an attacker-controlled header until
the signature has been checked. Trusting it earlier would let anyone short-circuit an ingest endpoint by
claiming to be its owner. When `Origin` equals this service's configured `Origin`, the request is a
message that has come home; the filter short-circuits with **200**, because a non-2xx would make the
sender retry a message it can never deliver.

### Freshness window

The verifier rejects a signature whose timestamp is outside ±`ClockSkewTolerance` of now.
**Default 5 minutes**, configurable per peer.

The signed timestamp only provides replay protection if someone enforces a window; without one a
captured request replays forever. Inbox deduplication covers replays of the same `message_id`, but the
filter is generic HTTP auth mounted on whatever endpoint an adopter chooses, and dedup only helps after
the request has been admitted and processed.

The canonical string is unchanged by this, so it has no interop impact.

**A stale timestamp answers 408, never 401.** This is the single most important status choice in the
spec, and it exists because the obvious answer is destructive.

A clock problem is not a credential problem. It is infrastructure — one bad NTP sync, one VM resumed
from a snapshot — and it self-heals the moment the clock corrects. But 401 is classified `Permanent`
(deliberately: retrying an identical signature is futile), and `OutboxDrainer` dead-letters a `Permanent`
result on the first attempt with no retries. So answering 401 for skew would mean a sender whose clock
drifts past five minutes has *every* message dead-lettered immediately — the queue drains straight into
dead rows, recoverable only by manually replaying everything sent during the drift.

Worse, that failure is indistinguishable from a compromised or rotated secret, so the operator's first
instinct is to rotate keys, which fixes nothing and wastes the window.

408 puts staleness in the `Transient` column instead: the messages retry on the outbox's backoff and
deliver themselves once the clock is right, with no operator action and no data movement. 401 stays
reserved for genuine signature and `kid` failures, where retrying really is futile and immediate
dead-lettering is the correct, visible outcome.

Both apps must be asked what they enforce today before a live channel cuts over — if they enforce no
window at all, adopting the framework could reject traffic that works today.

## Testing

The four supplied vectors are committed as a JSON fixture read at runtime, exercised in **both
directions** — the signer produces the expected signature, and the verifier accepts it. Drift then fails
a test rather than surfacing as a production 401, matching how both consumer repos already guard theirs.

Negative cases: tampered body, tampered path, reordered query, wrong key, unknown `kid`, unknown scheme,
and an oversized body rejected with 413 before any hashing. Expired and future timestamps are asserted
to answer **408, not 401** — that status is what keeps a clock-skew outage retryable instead of
dead-lettering the queue, so it is pinned by a test rather than left to a reviewer to notice.

Comparison uses `CryptographicOperations.FixedTimeEquals`. That is asserted by reading the call, not by
timing: a timing-based test is flaky and proves almost nothing at this granularity, so claiming one
would be worse than claiming nothing.

A **fifth vector** — body containing a newline and non-ASCII characters — will be generated here and
committed marked *candidate, unconfirmed by peers*. That case is currently unpinned on both consumer
sides, and it is not academic: `LeadForward.JsonOptions` uses `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`,
so Thai visitor names travel as raw UTF-8 rather than `\uXXXX` escapes. Both apps verify it against
their implementations before it is promoted.

## Deliberate trade-offs

**Routing lives in the sender's config.** The `type → path` map is per-peer configuration, so a peer
that moves an endpoint requires every sender to be redeployed. The alternative — carrying the path on
the envelope — is worse: `pathAndQuery` is signed, so a path travelling with the message would let the
publisher determine what gets signed, and a message sitting in an outbox for an hour would carry a route
that may no longer exist. Routing is a deployment concern, not a message property.

**`Retry-After` is ignored.** See *Response classification*. Deferred to its own request because
honouring it changes `DispatchResult`, which is already merged.

## Open questions

**Header prefix migration.** Neither consumer has mentioned that their current headers are
`X-Propertiezy-*`. They must confirm the prefix per channel before cutover.

**Freshness window today.** Neither has said whether their existing verifier enforces one. If they
enforce none and Themia defaults to 5 minutes, adopting the framework could reject traffic that works
today — and if *their* verifier answers 401 rather than 408 for staleness, a Themia sender talking to
their existing endpoint inherits the dead-letter-the-queue failure this spec designs against. Worth
asking explicitly, since it is their code that would produce the status.
