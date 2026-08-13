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

#### The timestamp format: two rules, and only one of them is about the sender

Added 2026-08-14 answering propertiezy's question on coord #0068 — *is a `+00:00` sender
non-conformant?* The answer needs both halves, because either alone is misleading.

1. **A sender MUST emit the trailing `Z` form** (`2026-07-14T09:30:00.0000000Z`), which is what
   `ThemiaHmacV1.FormatTimestamp` produces and what every golden vector carries. In .NET this means
   formatting `DateTimeOffset.UtcDateTime`, not the `DateTimeOffset` — the latter renders a zero offset
   as `+00:00`. A sender emitting `+00:00` is **off-spec**.

2. **A verifier MUST sign the literal header value it received, never a reformatted one.** The parse
   exists only to place the request in the freshness window. This is what keeps rule 1 from being
   load-bearing for interop: an off-spec sender still verifies.

So a `+00:00` sender is non-conformant but **not rejected**, and that is deliberate rather than an
oversight. ezy-assets' marketplace signer emitted `+00:00` from the day the channel was built until
2026-08-08 and nothing ever failed, because both verifiers echo (coord #0069). Rule 2 is the reason
there is no forced migration; rule 1 is the reason the wire converges anyway.

Rule 2 is the dangerous one to lose. A verifier that "normalises the timestamp before signing" 401s
every request from a non-`Z` sender — total, permanent, and indistinguishable from a rotated secret, so
the operator rotates the key and nothing changes. Pinned by
`HmacVerifierTests.Verify_ShouldSucceed_WhenTheSenderEmitsAZeroOffsetInsteadOfZ` and its naive-timestamp
sibling; falsified by making the verifier reformat, which fails exactly those two and nothing else.

### The rejection statuses are part of the scheme, not an implementation choice

A conformant `themia-hmac-v1` verifier — in any language, in or out of this repo — **must** answer:

| Condition | Status | Why this one |
|---|---|---|
| Timestamp outside the freshness window | **408** | A clock problem is infrastructure and self-heals; it must be retryable. |
| Timestamp missing or unparseable | **401** | Malformed input never becomes valid by retrying. |
| Signature mismatch, or `Key-Id` present but unknown | **401** | Retrying identical bytes fails identically. |
| Scheme header present but unrecognised | **400** | Protocol mismatch, not a credential failure. |
| Body over the size limit | **413** | Rejected before hashing; see *Body size limit*. |

This sits in the scheme definition rather than under *Receiving* because it is an **interop guarantee,
not a local preference**. A verifier that answers 401 for skew is a silent data-loss bug in any adopter
whose producer dead-letters 4xx — and that is the common case, because retrying a rejected signature
really is futile, so treating 4xx as permanent is the *correct* default for a producer. The two failures
are only separable if the verifier distinguishes them, and the only place that can be guaranteed is the
scheme both ends implement.

This was not designed in the abstract. Both live implementations shipped 401-for-skew, neither noticed,
and the resulting bug destroyed production data on one channel — see *Freshness window*. ezy-assets, on
fixing their half, asked for exactly this:

> *themia-hmac-v1 should pin this status split, not just the canonical string. A verifier that answers
> 401 for skew is a data-loss bug in any adopter whose producer dead-letters 4xx, and the framework is
> the right place to make that impossible to get wrong.*

Two independent implementations converged on this split after being bitten by its absence. It is
therefore normative, pinned by conformance tests, and not configurable.

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

| Header | Required | Purpose |
|---|---|---|
| `{prefix}Timestamp` | **yes** | The signed timestamp, byte-identical to the canonical string's first segment. |
| `{prefix}Signature` | **yes** | Lowercase hex. |
| `{prefix}Key-Id` | no | Selects which key verifies. |
| `{prefix}Scheme` | no | e.g. `themia-hmac-v1`. |
| `{prefix}Origin` | no | The originating system, for the loop guard. |

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

### Only two headers are mandatory, and that is not a default — it is the live wire format

propertiezy confirmed from source that the existing link sends **exactly two** headers on every channel
and both directions — `X-Propertiezy-Timestamp` and `X-Propertiezy-Signature` — defined once as
constants (`Propertiezy.Contracts.Security.HmacSigner.TimestampHeader` / `.SignatureHeader`) that both
the inbound filter and the outbound job reference. There is **no key-id header, no scheme header and no
origin header** anywhere in the live integration.

So the framework verifier must treat the other three as optional, with defined behaviour when absent:

| Absent header | Verifier behaviour |
|---|---|
| `Key-Id` | Try every inbound key configured for the peer, in registration order. Verification is constant-time per key, so this leaks nothing beyond "one of them matched". |
| `Scheme` | Assume `themia-hmac-v1`. A future v2 must be *explicitly* tagged; absence can never mean "the newest scheme", or adding v2 would silently reinterpret legacy traffic. |
| `Origin` | The loop guard **cannot run** — see below. |

A verifier that *required* `Key-Id` would reject the entire live link on the first request, which is the
same class of failure as the prefix mismatch: correct signature, rejected before it is ever checked.

Outbound signing always emits all five. propertiezy confirmed their filter reads only the two it knows
and ignores the rest, so emitting `Key-Id`/`Scheme`/`Origin` to a legacy endpoint is inert today and
becomes meaningful the moment that endpoint moves to the framework verifier. That is the migration path:
senders start emitting first, receivers start reading later, and no flag day is needed.

### Consequence: the loop guard is unavailable on legacy channels

The loop guard compares an `Origin` header that the live link neither sends nor reads. Until **both**
ends of a channel run the framework verifier, loop protection on that channel is not degraded — it is
**absent**.

This matters because #0050's stated goal includes bi-directional master-data sync, which is exactly the
topology a loop guard exists for. Enabling bi-directional flow on a channel where one end is still the
legacy verifier means a message that returns to its origin is accepted and re-processed rather than
dropped.

Two things follow, and both belong in the adoption checklist rather than in code:

1. `AddThemiaMessagingHmac` logs a **startup warning** naming any peer configured for bi-directional
   flow whose `Origin` header is not being read, so the gap is visible at boot rather than inferred from
   a cycling message.
2. Bi-directional sync must not be switched on for a channel until both ends verify with the framework.
   The inbox's `(origin, message_id)` deduplication limits the blast radius — a looped message is
   admitted once and dropped thereafter — but that is a backstop, not the guard, and it only helps for
   messages that go through the inbox.

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
3. Scheme header **present and unrecognised** → **400**. This is a protocol mismatch, not a bad
   credential. Scheme header **absent** → assume `themia-hmac-v1` and continue.
4. Timestamp missing or unparseable → **401**. Timestamp outside the freshness window → **408**
   (see *Freshness window* below — this status is load-bearing, not cosmetic).
5. `Key-Id` present but unknown for this peer → **401**. `Key-Id` absent → carry every inbound key
   configured for the peer into step 6.
6. Recompute the canonical string and compare with `CryptographicOperations.FixedTimeEquals` against
   each candidate key; no match → **401**.
7. **Then** the loop guard, if an `Origin` header is present. Absent → the guard cannot run.

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

**propertiezy confirmed they already enforce ±300 seconds** (`HmacAuthFilter.cs:40-53`,
`IngestAuthOptions.ToleranceSeconds`, range-constrained `[30, 3600]`) — identical to this default. So
there is no adoption surprise on those channels and the window must **not** be defaulted off for them:
the framework verifier would enforce exactly what they enforce today.

**The 401-for-staleness trap was real and already live in production, before any framework adoption.**
propertiezy's verifier answered 401 for an out-of-window timestamp, and ezy-assets' producer treats any
4xx as permanent — so a clock drift on the ezy-assets host silently dead-lettered every listing snapshot,
presenting to the operator as a compromised secret. That is the exact failure this section designs
against, reached without Themia being involved at all.

propertiezy has shipped their half (408 for staleness, 401 retained for genuine signature failure,
unparseable timestamps deliberately still 401 since retrying cannot make them valid) and filed **coord
#0051** asking ezy-assets to classify 408 as transient — without which the fix changes nothing, because
408 is not in ezy-assets' documented status list and an unrecognised 4xx treated as permanent
dead-letters just the same.

They explicitly declined the offered fallback of classifying 401 as transient for these peers, on the
grounds that it would weaken the one classification deliberately made strict. **Do not build it.**

**ezy-assets have since shipped both halves of their side** (PR #169): their delivery producer now
classifies 408 and 429 as transient alongside 5xx, and their own lead verifier — which had the same
defect, collapsing missing headers, unparseable timestamps, stale timestamps and signature mismatches
all into 401 — now answers 408 for staleness and retains 401 for the rest. They reproduced the
dead-lettering with a failing test before changing anything.

Two things from their reply that bear on this spec:

**The blast radius was one channel, not two.** propertiezy's original report said entitlement pushes
were destroyed alongside listing snapshots. They were not: `EntitlementSyncJob` has no terminal-failure
state — a non-2xx increments a counter and leaves the tenant due, so it retries on the next fire
regardless of status. Reassert-based sync, not an outbox. Recorded so the corrected version is what
survives.

**Their reason for declining 401-as-transient does not transfer to Themia adopters.** They held 401
strict because their outbox has no alert when a row retries forever — their spec promises one and the
code never implemented it — so retrying 401 indefinitely would trade a silent destroy for a silent
stall. Themia's outbox has no such gap: `MaxAttempts` bounds retries and the row dead-letters, so a
transient classification here cannot stall indefinitely. Their constraint is real and specific to their
system; it is not an argument against this design.

## Testing

The four supplied vectors are committed as a JSON fixture read at runtime, exercised in **both
directions** — the signer produces the expected signature, and the verifier accepts it. Drift then fails
a test rather than surfacing as a production 401, matching how both consumer repos already guard theirs.

Negative cases: tampered body, tampered path, reordered query, wrong key, unknown `kid`, unknown scheme,
and an oversized body rejected with 413 before any hashing. Expired and future timestamps are asserted
to answer **408, not 401** — that status is what keeps a clock-skew outage retryable instead of
dead-lettering the queue, so it is pinned by a test rather than left to a reviewer to notice.

**Legacy two-header requests are a first-class test case, not an edge case** — they are what the live
link actually sends. A request carrying only `{prefix}Timestamp` and `{prefix}Signature`, with no
`Key-Id`, `Scheme` or `Origin`, must verify successfully against a peer with one inbound key, and must
still verify against a peer with several. A test also pins that an absent `Scheme` is treated as v1
rather than as "newest", since that is what stops a future v2 from silently reinterpreting legacy
traffic.

Comparison uses `CryptographicOperations.FixedTimeEquals`. That is asserted by reading the call, not by
timing: a timing-based test is flaky and proves almost nothing at this granularity, so claiming one
would be worse than claiming nothing.

A **fifth vector** — body containing a newline and non-ASCII characters — will be generated here and
committed marked *candidate, unconfirmed by peers*. That case is currently unpinned on both consumer
sides, and it is not academic: `LeadForward.JsonOptions` uses `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`,
so Thai visitor names travel as raw UTF-8 rather than `\uXXXX` escapes. Both apps verify it against
their implementations before it is promoted.

Use **Thai script** for the non-ASCII content, at propertiezy's suggestion — a visitor name in Thai is
the actual traffic on the lead channel, so the vector pins the real case rather than a synthetic one.

## Deliberate trade-offs

**Routing lives in the sender's config.** The `type → path` map is per-peer configuration, so a peer
that moves an endpoint requires every sender to be redeployed. The alternative — carrying the path on
the envelope — is worse: `pathAndQuery` is signed, so a path travelling with the message would let the
publisher determine what gets signed, and a message sitting in an outbox for an hour would carry a route
that may no longer exist. Routing is a deployment concern, not a message property.

**`Retry-After` is ignored.** See *Response classification*. Deferred to its own request because
honouring it changes `DispatchResult`, which is already merged. propertiezy confirmed their ingest
endpoints are not rate-limited, so no live channel is affected today; their rate-limited
`POST /api/v1/leads` is first-party BFF traffic, not an inter-service channel.

## Settled by propertiezy (coord #0050)

**Headers:** `X-Propertiezy-Timestamp` and `X-Propertiezy-Signature`, identical on both channels and
both directions, no key-id, no origin. Set the per-peer prefix to `X-Propertiezy-` for both legacy
channels. Adding `Key-Id`/`Scheme`/`Origin` outbound is inert against their current filter, which reads
only the two it knows.

**Freshness window:** enforced today at ±300 seconds, matching this default. Do not default it off.

**Stale-timestamp status:** both halves shipped — propertiezy PR #36, ezy-assets PR #169 (coord #0051).
Both live implementations now match the normative split above.

## Open questions

**ezy-assets has not answered on #0050.** They replied fully on #0051, but the questions addressed to
them here are still open: Q1–Q3 for the ingest side they own, plus whether they reference
`DrainSignal`/`BackoffPolicy`, the blocking `CREATE INDEX` on their notifications outbox at upgrade, and
whether they filter notification logs on the `{Channel}` facet.

The `DrainSignal` answer is the one that gates a release: it is a source break for any 0.10.x consumer,
propertiezy confirmed they do not touch it, and ezy-assets is the only remaining unknown. If they do not
use it either, the generic reshape is free.

**Their verifier does not log skew or tolerance on rejection** — `LeadsController` has no logger
injected, so if their clock drifts, the *sending* side sees 408s and retries correctly while their own
operator sees nothing locally. Not a Themia concern, but the framework verifier should log both values
on a 408 so an adopter can separate "clock problem" from "attack" in one line, which is what
propertiezy's fix does and theirs does not.
