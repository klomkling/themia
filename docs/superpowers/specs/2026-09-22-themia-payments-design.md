# Themia.Payments — design

**Status:** proposed scope, not yet implemented.
**Target version:** `0.30.0` (core + Beam adapter together; a core with no adapter ships nothing usable).
**Evidence:** three consumers need online payment collection — ezy-assets (subscription), propertiezy
(boost / slot packs, coord #0052), opsezy. One provider is chosen today: **Beam** (`beamcheckout.com`).
**Prior art:** the pre-Themia `EzyAssets.Interfaces.PaymentGateway` + `.ChillPay` + `.TwoCTwoP` trio in
`backup-repo/ezy-assets`. That abstraction survived two real providers on two methods; §10 lists what it
got right and the eight things this design deliberately does differently.

---

## 1. What this is

A provider-agnostic seam for **collecting a payment and learning the outcome**, plus one adapter.

```
Themia.Payments        net8.0;net10.0   IPaymentGateway, IPaymentWebhookVerifier, Money, PaymentStatus
Themia.Payments.Beam   net8.0;net10.0   BeamPaymentGateway, BeamWebhookVerifier, + Beam-only surface
```

It is **not** billing. Prices, plans, entitlements, invoices, tax, settlement reconciliation and refund
policy are app domain and stay in the apps — the same boundary `Themia.PromptPay` already states for
itself ("Out of scope, permanently").

**No `Themia.Modules.Payments`.** Nothing here is tenant-scoped or persistent: one merchant account per
app (decided 2026-09-22), no table, no migration, no `IThemiaModule` lifecycle. Each app stores its own
order ↔ charge mapping, because only the app knows what the charge was *for*. If per-tenant merchant
credentials ever appear, that is the moment a module becomes necessary — not before.

---

## 2. Why a core abstraction at all, with one provider

Two packages cost more than one, and "one provider" is normally the argument against an abstraction.
It does not hold here:

- **A second provider is already named.** 2C2P and Stripe are both under consideration, and ezy-assets
  ran ChillPay **and** 2C2P simultaneously before Themia existed.
- **The realistic case is *add*, not *replace*.** Beam covers PromptPay and Thai wallets; a card-heavy or
  cross-border flow lands on another provider. Two live providers need a selection point, which is the
  abstraction.
- **Even a clean switch runs both.** Charges created before the cutover must still be queried and
  refunded through the old provider for months.

What it does **not** buy: portability of everything. §6 lists what stays provider-shaped on purpose.

---

## 3. `Money`

```csharp
public readonly record struct Money(long MinorUnits, string Currency)
{
    public static Money Thb(long minorUnits);
}
```

**Minor units, not `decimal`.** Beam takes `amount: 10000` for 100.00 THB, and its webhook example carries
`"amount": 199`. Every provider in this space is integer-minor-unit on the wire. The old
`OrderModel.Amount` was a `decimal` with **no currency at all**, which put both the rounding and the
currency in the caller's head at every call site. Currency is ISO 4217, validated on construction,
compared ordinally.

---

## 4. `IPaymentGateway` — three operations

```csharp
Task<ChargeCreation> CreateChargeAsync(CreateChargeRequest request, CancellationToken ct = default);
Task<Charge>         GetChargeAsync(string chargeId, CancellationToken ct = default);
Task<RefundCreation> RefundAsync(RefundRequest request, CancellationToken ct = default);
```

The old interface had exactly two — `RequestToken` (start a payment) and `Inquiry` (ask its status) — and
those two were enough for ChillPay and 2C2P. This keeps both, async with a `CancellationToken`, and adds
refund, which the old one lacked and every consumer here needs.

### `CreateChargeRequest`

| Field | Notes |
| --- | --- |
| `Amount` | `Money`. |
| `ReferenceId` | The app's own order id. The only field the app can find its order by when a webhook arrives. Required. |
| `Method` | `PaymentMethod` enum — v1: `QrPromptPay`, `HostedCheckout`. |
| `ReturnUrl` | Where the shopper lands after an off-site step. Optional. |
| `ExpiresAt` | When the QR / link stops being payable. Optional. |
| `IdempotencyKey` | **Caller-supplied and stable across retries** (§5). Optional; generated per call when absent, which is only safe because a generated key is used for that call's internal retries too. |

### `ChargeCreation` — the next action is part of the contract

```csharp
public sealed record ChargeCreation(string ChargeId, PaymentStatus Status, NextAction Action);

public abstract record NextAction
{
    public sealed record None : NextAction;
    public sealed record Redirect(Uri Url) : NextAction;
    public sealed record ShowQr(byte[] ImagePng, string? RawPayload, DateTimeOffset? Expiry) : NextAction;
}
```

Beam answers `actionRequired` as `NONE` / `REDIRECT` / `ENCODED_IMAGE`; 2C2P returns a redirect URL;
Omise returns `authorize_uri` or a QR image. The three-way shape is the common denominator, not a Beam
detail. A closed hierarchy (not a nullable-field bag) so a consumer's `switch` breaks when a fourth
action appears.

### `PaymentStatus`

`Pending` · `Succeeded` · `Failed`. Beam's own lifecycle, and the intersection of the others.

**`Pending` is not a transient state you may wait on.** Beam documents that an abandoned charge can stay
`PENDING` **indefinitely** — the shopper closes the QR screen and nothing further happens. Any consumer
loop that waits for a final status must carry its own timeout and treat the timeout as unpaid, while
keeping the charge id, because a very late `charge.succeeded` is still possible. This is written into the
XML docs of `PaymentStatus.Pending`, not just here: it is the single most likely way an adopter hangs a
booking or a slot allocation forever.

No `Canceled` / `Expired` member yet. A provider that needs one adds it, and the exhaustive `switch`
that breaks at every consumer is the point (`dotnet.md`: new state = enum member, never a second bool).

### Failure

```csharp
public sealed record Charge(string ChargeId, string ReferenceId, Money Amount,
                            PaymentStatus Status, PaymentFailure? Failure, DateTimeOffset? CompletedAt);

public sealed record PaymentFailure(FailureReason Reason, string ProviderCode, string? ProviderMessage);
```

`FailureReason`: `Unknown` · `ProcessingFailed` · `InsufficientFunds` · `AuthenticationFailed` ·
`Declined` · `Expired`. The adapter maps; the raw `ProviderCode` is always carried, because the normalized
reason will be `Unknown` for codes we have not seen and an operator still needs the real one.

**A failed payment is not a failed call.** Beam returns HTTP 2xx for a charge that fails — the request was
processed correctly. Transport/validation problems throw (§5); a declined payment does not.

---

## 5. Errors, retries, idempotency

- **Transport and API errors throw** `PaymentApiException(FailureKind, string providerCode, int httpStatus)`.
  `FailureKind`: `Authentication` · `Validation` · `NotFound` · `Permission` · `RateLimited` · `Transient` · `Unknown`.
- **Retry only `5xx`, timeouts and `429`**, with exponential backoff + jitter, capped at 3 attempts —
  Beam documents this exact policy and answers `429 TOO_MANY_REQUESTS_ERROR`. Never retry a 4xx.
- **A retry must carry the same idempotency key.** Beam keys are header `x-beam-idempotency-key`, ≤255
  chars, remembered for **12 hours**, and cover `POST`/`PATCH` only. Generating a fresh key per attempt is
  how a retry becomes a second charge; the key is therefore built once per logical operation, above the
  retry loop, and the test suite pins that.
- Never log the API key, the HMAC key, or a full request/response body (`security.md`); log the charge id,
  the reference id, the provider code and the HTTP status.

---

## 6. Webhooks — the part that must not be written three times

```csharp
public interface IPaymentWebhookVerifier
{
    WebhookVerification Verify(ReadOnlySpan<byte> rawBody, IReadOnlyDictionary<string, string> headers);
}

public sealed record WebhookVerification(WebhookOutcome Outcome, PaymentEvent? Event);
// WebhookOutcome: Verified · SignatureMissing · SignatureMismatch · UnknownEvent · Malformed
```

`PaymentEvent`: `Type` (`ChargeSucceeded` · `ChargeFailed` · `RefundSucceeded` · `RefundFailed` · `Other`),
`ChargeId`, `ReferenceId`, `Amount`, `Status`, `OccurredAt`, and the raw JSON for anything provider-specific.

Three hard requirements, each of which is a way to get this wrong:

1. **The signature covers the exact bytes received.** Beam signs the raw, unformatted body and says so
   explicitly. Any pipeline that deserializes and re-serializes before verifying will mismatch. The verifier
   therefore takes bytes, never a parsed object, and an ASP.NET Core host must call `EnableBuffering()` and
   read the body before model binding.
2. **Constant-time comparison** (`CryptographicOperations.FixedTimeEquals`) over the decoded signature.
3. **Verification proves origin, not freshness.** Beam's `X-Beam-Signature` is `base64(HMAC-SHA256(body))`
   with **no timestamp and no nonce**, so a captured request replays forever. The verifier cannot fix that
   and must not pretend to: `Verified` means authentic. Deduplication is the consumer's, on
   `(chargeId, status)` or the refund id, and the XML docs say so. This is stated because our own HMAC work
   on coord #0068/#0069 found the opposite trap — a signature that *did* cover a timestamp whose **format**
   no test exercised.

**Golden vector.** Beam publishes a signature, an HMAC key and the exact body that produce it. That triple
is pinned as a test the way `Themia.PromptPay`'s CRC vectors and `Themia.Messaging`'s HMAC vector are — and
unlike those, this one is supplied by the provider, so it also detects our own byte handling drifting from
theirs.

---

## 7. `Themia.Payments.Beam`

| Concern | Decision |
| --- | --- |
| Auth | HTTP Basic, `base64(merchantId:apiKey)`. Partner mode adds `X-Beam-Partner-ID` — supported, unused today. |
| Environments | `https://api.beamcheckout.com` and `https://playground.api.beamcheckout.com`, selected by a `BeamEnvironment` enum, never a free-text URL. |
| Options | `BeamOptions { MerchantId, ApiKey, Environment, WebhookHmacKey, PartnerId?, Timeout }`, bound from configuration, `ValidateOnStart`. No `IConfiguration` reads anywhere else in the package. |
| HTTP | Named `IHttpClientFactory` client. No `HttpClient` construction. |
| Registration | `services.AddThemiaPaymentsBeam(...)` registers `IPaymentGateway` + `IPaymentWebhookVerifier`. Keyed registration when more than one provider is present. |

Endpoints used: `POST /api/v1/charges`, `GET /api/v1/charges/{id}`, `POST /api/v1/refunds`,
`GET /api/v1/refunds/{id}`, `POST /api/v1/payment-links`, `GET /api/v1/payment-links/{id}`,
`PATCH /api/v1/payment-links/{id}/disable`, `POST /api/v1/charges/verify-qr-slip`.

### Beam-only surface — on the concrete type, not the interface

`BeamPaymentGateway` implements `IPaymentGateway`. Everything below is on `BeamPaymentClient`, reachable
only by an app that references this package deliberately:

- **Payment Links** — hosted checkout. Create / get / disable; a link is immutable otherwise. Modelled
  separately from a charge because a link *produces* charges (`source: PAYMENT_LINK`).
- **QR slip verification** — `multipart/form-data`, `format=RAW` (the QR content off the slip) or `IMAGE`.
  Two traps the wrapper enforces: it is the QR **on the slip**, not the one the shopper scanned; and
  `verificationResult` never reports failure — an unverifiable slip is a `4xx`, so the HTTP status is the
  answer. This fills the hole `Themia.PromptPay` documents as "slip verification lives elsewhere".

### Refund semantics worth pinning

`amount` omitted or `0` means "the maximum refundable amount", and **partial refunds exist only for `CARD`
charges** — a QR PromptPay charge refunds in full or not at all. A `RefundRequest` with a partial amount
against a non-card charge is rejected by the adapter before the call, with the reason, rather than
surfacing as a provider validation error.

### Mapping table (adapter's whole job)

| Beam | Core |
| --- | --- |
| `PENDING` / `SUCCEEDED` / `FAILED` | `PaymentStatus.Pending` / `.Succeeded` / `.Failed` |
| `actionRequired` `NONE` / `REDIRECT` / `ENCODED_IMAGE` | `NextAction.None` / `.Redirect` / `.ShowQr` |
| `CH_PROCESSING_FAILED`, `CH_INSUFFICIENT_FUNDS`, `CH_AUTHENTICATION_FAILED`, `CH_CARD_*` | `FailureReason` + raw code |
| `X-Beam-Event` `charge.succeeded` / `charge.failed` / `refund.*` | `PaymentEventType` |
| `card_authorization.*`, `bolt_intent.*`, `transaction.created`, `payment_link.paid` | `Other` (still `Verified`) |
| `INVALID_CREDENTIALS_ERROR` / `API_VALIDATION_ERROR` / `NOT_FOUND_ERROR` / `NO_PERMISSION_ERROR` / `TOO_MANY_REQUESTS_ERROR` | `FailureKind` |

---

## 8. Deliberately not in v1

Cards, 3DS, card tokenization, CIT/MIT, installments, Beam Bolt devices, store links, transactions and
settlement reports. Cards drag PCI scope and a second flow (`skip3dsFlow`, authorization/capture) that no
consumer has asked for; the other three are Beam-only concepts with no second implementation in sight.

Also out: an HTTP endpoint. A `Themia.Payments.AspNetCore` with a mapped webhook endpoint, `EnableBuffering`
and a verification filter is a reasonable phase 2 — it is where the raw-body mistake is easiest to make —
but only after one app has run the hand-wired version and we know the shape of what it needs.

---

## 9. Testing

- **Golden vector** from Beam's docs for the webhook signature, byte-exact, plus a mutation test: one
  changed byte in the body must produce `SignatureMismatch`.
- A test that the verifier is never handed a re-serialized body (the signature check runs on the captured
  bytes fixture, and the fixture round-trips through `JsonSerializer` in a *negative* test that must fail).
- Minor-unit mapping both directions, including `199` and `10000`, and a currency mismatch rejection.
- Retry policy: `429` and `503` retried with the **same** idempotency key (asserted on the captured
  requests), `400` not retried, attempts capped.
- Status/failure/action mapping tables driven by recorded Beam payloads.
- No "wait until final" helper is shipped, so no test pins one: a charge can stay `Pending` for ever (§4),
  and a helper that loops is a hang waiting for an adopter to inherit.
- Live calls against Playground are integration tests, skipped unless `THEMIA_BEAM_*` env vars are set;
  CI runs the unit suite only.

---

## 10. What the prior art got right, and the eight changes

Right, and kept: the two-method interface; one package per provider; provider chosen by configuration.

Changed:

1. **No hub that references every provider.** `EzyAssets.Interfaces.Gateway` `new`-ed `ChillPayService` and
   `TwoCTwoPService` in a `switch`, so the shared package depended on all providers and a third meant editing
   it. Here each adapter registers itself; the core references nothing.
2. **Async end-to-end** with `CancellationToken`; the old interface was synchronous HTTP.
3. **Webhooks exist.** The old design could only poll (`Inquiry`).
4. **Refund exists.**
5. **No `UserDefined1..5`.** 2C2P's field names had leaked onto the shared `InquiryResponse`; provider-shaped
   data lives behind the provider's own type or the raw JSON on `PaymentEvent`.
6. **Outcome is an enum, not a derived bool.** `IsSuccess => string.IsNullOrEmpty(ErrorMessage)` made "why"
   unreachable; `PaymentStatus` + `PaymentFailure` do not.
7. **Typed, validated options.** The old services each read `IConfiguration` internally; `PaymentGatewaySetting`
   carried only `Default`.
8. **`Money`, not a bare `decimal`.**

---

## 11. Decisions — do not relitigate

- **Two packages from day one** (core + Beam), for the reasons in §2. Adding 2C2P or Stripe later is a new
  adapter package and a DI line, not an app change.
- **No module, no store, no tenant scoping** while credentials are one account per app.
- **`Themia.PromptPay` does not move under this family and is not renamed.** It builds an EMVCo payload
  offline — no credentials, no charge, no status, no webhook — so it cannot implement `IPaymentGateway`, and
  a `Themia.Payments.PromptPay` name would promise a lifecycle it does not have. The QR-vs-gateway split is
  a settlement and trust decision the app makes; see `2026-09-22-themia-emvcoqr-design.md`.
- **Payment Links and slip verification stay on the Beam type**, not the interface, until a second provider
  offers the same thing.
