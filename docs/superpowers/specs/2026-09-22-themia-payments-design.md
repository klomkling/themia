# Themia.Payments — design

**Status:** proposed scope, not yet implemented.
**Target version:** `0.30.0` (core + both adapters together; a core with no adapter ships nothing usable,
and one adapter cannot show whether the core generalises).
**Evidence:** three consumers need online payment collection — ezy-assets (subscription), propertiezy
(boost / slot packs, coord #0052), opsezy. The provider chosen for new work is **Beam** (`beamcheckout.com`); **2C2P** is ported from
production code ezy-assets already ran.
**Prior art:** the pre-Themia `EzyAssets.Interfaces.PaymentGateway` + `.ChillPay` + `.TwoCTwoP` trio in
`backup-repo/ezy-assets`. That abstraction survived two real providers on two methods; §10 lists what it
got right and the eight things this design deliberately does differently.

---

## 1. What this is

A provider-agnostic seam for **collecting a payment and learning the outcome**, plus two adapters.

```
Themia.Payments          net8.0;net10.0   IPaymentGateway, IPaymentWebhookVerifier, Money, PaymentStatus
Themia.Payments.Beam     net8.0;net10.0   BeamPaymentGateway, BeamWebhookVerifier, + Beam-only surface
Themia.Payments.TwoCTwoP net8.0;net10.0   2C2P PGW 4.3 (Redirect API), JWT transport
```

Two adapters, not one. 2C2P is in scope from the start because **ezy-assets already ran it in production**
(`EzyAssets.Interfaces.TwoCTwoP`, PGW Redirect API) — the integration is a port, not a discovery, and a
second real adapter is the only way to know whether the core is genuinely provider-shaped. §7b lists the
four places 2C2P moved the core's design, which is the return on building both.

It is **not** billing. Prices, plans, entitlements, invoices, tax, settlement reconciliation and refund
policy are app domain and stay in the apps — the same boundary `Themia.PromptPay` already states for
itself ("Out of scope, permanently").

**Settlement shape (decided 2026-09-22).** Every payment lands in the platform's own merchant account;
agents and landlords are paid out later by periodic transfer, not by the shopper paying them directly. So
there is no per-tenant merchant onboarding, and no flow where Themia must hold someone else's credentials.

> **ANSWERED — and it constrains what a payment here is allowed to be.** KBank's Thai QR merchant
> conditions flagged it (*"กรณีที่บริษัทให้บริการในรูปแบบ Platform รับเงินแทน แล้วจ่ายเงินให้ร้านค้าที่ใช้บริการ Platform
> ภายหลัง บริษัทต้องมีใบอนุญาต..."*), and counsel answered it for opsezy on 2026-09-22: a platform that
> receives a customer's payment for work done by an agent, holds it, and settles later **is** a regulated
> payment business under the Payment Systems Act B.E. 2560 (s.16 — payment agent / payment facilitator),
> and operating without a licence carries criminal penalties. This is a rule about the flow of funds, so no
> choice of provider avoids it.
>
> Three compliant structures, all of which keep this spec's design intact:
>
> 1. **Direct payment + invoiced commission** (what opsezy ran until 2026-09-23). The customer pays the **agent** directly —
>    the platform only shows the agent's account or PromptPay QR — and the platform separately invoices the
>    agent for commission. The only charge Themia creates is that commission: the platform's **own**
>    revenue, one merchant account, one party. Exactly the shape §1 assumes.
> 2. **Licensed-PSP split payment.** Agents onboard as sub-merchants of a licensed PSP; the PSP holds and
>    splits. The platform only *instructs* the split. See §8 for what this would add.
> 3. **Principal / subcontractor** (opsezy's choice, 2026-09-23). The platform contracts with the customer
>    itself, so the receipt is its own revenue; it then pays agents as subcontractors. Same engineering
>    shape as (1), different tax (VAT on gross) and full liability for the work.
>
> **Per consumer, as answered so far:**
>
> | App | Structure | Status |
> | --- | --- | --- |
> | opsezy | **(3) principal / subcontractor** — chosen 2026-09-23 after the founders compared the three | the customer contracts with the platform and pays it; the platform hires the technician under a works contract. The charge Themia creates is the **full job price**, and it is the platform's own revenue. Structure (1) was what they ran before this decision |
> | propertiezy | own revenue only — Boost / slot packs sold to agents | counsel answered 2026-09-22: **not in scope of the Act at all**, no licence. Their Terms of Use also state they take no money, deposit or consideration from buyers or renters, so the constraint is contractual as well as regulatory |
> | ezy-assets | SaaS subscription + add-ons, and a **zero-custody** commission ledger | counsel answered 2026-09-22: subscriptions and add-ons are own revenue, **safe**; the Phase 5 commission feature is safe *because* the platform never touches deal money — the agency transfers to the agent and the system only records proof |
>
> All three consumers answered, and all three land in the same place: **every charge `IPaymentGateway`
> creates is the platform's own revenue** — subscription, add-on, boost, or the full price of a job the
> platform itself contracted for. A charge that
> collects someone else's money is out of scope by law, not by preference, and (2) is the only route that
> would change this interface. The tax side (receipt / tax invoice, 3% withholding when the payer is a
> company, VAT registration past the threshold, and VAT on **gross** under structure (3)) is app domain and
> does not enter this package.
>
> Two constraints from the same advice that land on *other* Themia packages, recorded here because this is
> where the reasoning lives:
>
> - **Payout proof and slip images are high-risk personal data** (bank account numbers, transfer slips, an
>   ID card where a 50-ทวิ is issued). They must be stored privately and reached only by an authorised role
>   — never at a permanent unsigned URL. In Themia terms that means `StorageVisibility.Private` plus
>   `IStorageProvider.GetPresignedUrlAsync`, and **not** `GetPublicUrl`, which exists for listing photos.
>   A consumer that reaches for the public-URL feature here has a PDPA problem, not a storage problem.
> - **AML.** Real-estate brokerage is a reporting entity under the Anti-Money Laundering Act; a platform
>   that starts receiving deal or deposit money inherits CDD/KYC and suspicious-transaction reporting. That
>   is another reason the zero-custody line is not merely a licensing technicality.

**No `Themia.Modules.Payments`.** Nothing here is tenant-scoped or persistent: one merchant account per
app, no table, no migration, no `IThemiaModule` lifecycle. Each app stores its own
order ↔ charge mapping, because only the app knows what the charge was *for*. If per-tenant merchant
credentials ever appear, that is the moment a module becomes necessary — not before.

---

## 2. Why a core abstraction and not one package per app

An abstraction with a single implementation is usually premature. That objection does not apply here:

- **The second provider is not hypothetical — it is being built here.** ezy-assets ran ChillPay **and**
  2C2P simultaneously before Themia existed, and the 2C2P adapter in §7b is a port of that code.
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
Task<Charge>         GetChargeAsync(ChargeRef charge, CancellationToken ct = default);
Task<RefundCreation> RefundAsync(RefundRequest request, CancellationToken ct = default);
```

`ChargeRef` is **not** a `string chargeId`, and that is 2C2P's doing: Beam reads a charge by its own
`chargeId` (`GET /api/v1/charges/{id}`), while 2C2P's Payment Inquiry takes the merchant's own `invoiceNo`
and has no provider-side id to ask by before payment. A single-provider design would have hard-coded the
Beam shape here and broken on the first port.

```csharp
public readonly record struct ChargeRef(string? ProviderChargeId, string ReferenceId);
```

The app always holds `ReferenceId`; `ProviderChargeId` is filled when the provider issued one. Adapters use
whichever they support and never silently ignore the other.

The old interface had exactly two — `RequestToken` (start a payment) and `Inquiry` (ask its status) — and
those two were enough for ChillPay and 2C2P. This keeps both, async with a `CancellationToken`, and adds
refund, which the old one lacked and every consumer here needs.

### `CreateChargeRequest`

| Field | Notes |
| --- | --- |
| `Amount` | `Money`. |
| `ReferenceId` | The app's own order id. The only field the app can find its order by when a webhook arrives. Required. |
| `AllowedMethods` | The methods this charge may be paid with, as a set (§4b). **One element** means a direct charge with that method; **more than one** means a hosted page offering the choice. Both providers work this way: Beam names a single `paymentMethodType` on a charge and enables groups on a payment link, 2C2P takes `paymentChannel[]`. |
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

### `PaymentMethod`

`QrPromptPay` · `Card` · `MobileBanking` · `Wallet`. The four that both adapters can serve and that differ in
cost. Finer distinctions stay provider-side: Beam's `KPLUS` / `SCB_EASY` / `KRUNGSRI_APP` / `BANGKOK_BANK_APP`
are all `MobileBanking`, and `TRUE_MONEY` / `LINE_PAY` / `SHOPEE_PAY` / `ALIPAY` are all `Wallet`. An adapter
declares which it supports, and startup fails if the configuration can ask for one it cannot serve (§4b).

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
`Declined` · `Expired` · `Canceled`. The adapter maps; the raw `ProviderCode` is always carried, because the
normalized reason will be `Unknown` for codes we have not seen and an operator still needs the real one.

`Canceled` exists because of 2C2P (`0003 Transaction is cancelled`); Beam alone would not have produced it.
Second-provider pressure on the core is the point of §2, and this is the cheapest example of it.

**A failed payment is not a failed call.** Beam returns HTTP 2xx for a charge that fails — the request was
processed correctly. Transport/validation problems throw (§5); a declined payment does not.

---

## 4b. Method policy by amount — optional, and off unless configured

The fee on a payment depends on how it is paid: a QR transfer is cents or a flat fee, a card is a percentage.
On a 200 THB charge the card fee can be a tenth of the whole amount, and on a 50,000 THB one a customer who
cannot use a card may simply not pay. Every adopter here ends up wanting the same rule with different numbers:
*small amounts, cheap methods only; above a threshold, open up the expensive ones.*

```csharp
public sealed record PaymentMethodBand(long UpToMinorUnitsInclusive, IReadOnlyList<PaymentMethod> Methods);

public sealed class PaymentMethodPolicy
{
    public string Currency { get; init; } = "THB";
    public IReadOnlyList<PaymentMethodBand> Bands { get; init; } = [];
    public IReadOnlyList<PaymentMethod> Above { get; init; } = [];   // beyond the last band

    public IReadOnlyList<PaymentMethod> Resolve(Money amount);
}
```

```jsonc
// 1,000 THB and under: QR or a banking app. Above that, cards as well.
"Payments": {
  "MethodPolicy": {
    "Currency": "THB",
    "Bands":  [ { "UpToMinorUnitsInclusive": 100000, "Methods": [ "QrPromptPay", "MobileBanking" ] } ],
    "Above":  [ "QrPromptPay", "MobileBanking", "Card" ]
  }
}
```

**Thresholds are in minor units**, because `Money` is (§3): `100000` is 1,000.00 THB. Writing `1000` there
means ten baht, which is why the property name says so rather than being called `UpTo`.

Rules, each one a failure mode we would otherwise ship:

- **Off by default.** No `MethodPolicy` configured means the caller's `AllowedMethods` is used unchanged. This
  is a policy an adopter opts into, not a default that quietly narrows what a charge accepts.
- **Validated at startup** (`ValidateOnStart`), not at the first payment: bands strictly ascending, no
  duplicates, no empty method list, `Above` non-empty, and every method named must be one the **registered
  adapter declares it supports**. A policy that can produce `Wallet` against an adapter without wallets is a
  configuration error, and the only safe time to find it is boot.
- **Inclusive upper bound**, spelled in the property name, with a test at exactly the boundary. "≤ 1,000" and
  "< 1,000" differ by one charge a day at the threshold, and the argument is unwinnable after the fact.
- **Currency-scoped.** `Resolve` on a `Money` of another currency throws rather than applying baht bands.
- **Intersection, not replacement.** The result is `caller ∩ policy`. A caller asking for `Card` only, on an
  amount where the policy forbids it, gets an empty set — which **throws** with both lists in the message. It
  does not silently fall back to the policy's methods, because a caller that asked for one method usually has
  a reason (a saved card, a retry of a failed attempt).
- **No store, no ledger.** Pure function of amount and configuration.

What it deliberately does **not** do: pick the cheapest method, reorder what the shopper sees, or model fees.
Fee schedules are per-merchant contract terms, they change without a release, and a wrong one silently costs
money — so the package takes the rule the adopter writes and does not try to compute it.

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

## 7b. `Themia.Payments.TwoCTwoP`

2C2P **PGW API 4.3, Redirect flow** — the flow ezy-assets already ran: request a payment token, send the
shopper to the hosted page, learn the outcome from the backend notification, and inquire as the fallback.

| Concern | Decision |
| --- | --- |
| Transport | Every request body is `{"payload": "<JWT>"}` and every response is the same. **JWT HS256 signed with the merchant secret key** — the signature *is* the authentication; there is no bearer or basic header. |
| Endpoints | `https://pgw.2c2p.com/payment/4.3/paymentToken` and `/paymentInquiry`; sandbox `https://sandbox-pgw.2c2p.com/...`. Selected by a `TwoCTwoPEnvironment` enum. |
| Options | `TwoCTwoPOptions { MerchantId, SecretKey, Environment, PaymentChannels, Timeout }`, validated on start. |
| Create | `paymentToken` with `merchantID`, `invoiceNo` (the app's `ReferenceId`, **AN 20 max**), `description`, `amount`, `currencyCode`, `paymentChannel[]`, `backendReturnUrl`, `frontendReturnUrl`. Response: `paymentToken`, `webPaymentUrl`, `respCode`, `respDesc` → `NextAction.Redirect(webPaymentUrl)`. |
| Read | `paymentInquiry` by `invoiceNo` (or `paymentToken`) + `locale`. |
| Refund / void | **Payment Process API**, `processType` (e.g. `I` settle, `V` void/refund) with `invoiceNo`, `actionAmount` and `idempotencyID`. Note this API is **XML**, not JSON, while the rest of PGW 4.3 is JWT-over-JSON — the adapter isolates that, and nothing about it reaches the core. |
| Notification | 2C2P POSTs the payment result to `backendReturnUrl` as a JWT signed with the same secret. |

### The four places 2C2P changed the core

1. **`ChargeRef` instead of `string chargeId`** (§4) — 2C2P has no provider id to read by.
2. **`FailureReason.Canceled`** (§4) — `0003`.
3. **`RefundCreation.RefundId` is nullable.** Beam returns `refundId`; 2C2P's Payment Process answers the
   action without minting a refund object, so a consumer keying refunds by provider id would break on port.
4. **The webhook verifier takes raw bytes and a header bag, and returns an outcome enum** (§6) — it is not
   an "HMAC header" interface. Beam verifies `X-Beam-Signature` over the body; 2C2P verifies the JWT
   signature of the body itself and uses no header at all. Both fit without changing the interface, which
   is the strongest available evidence that §6 is not Beam-shaped.

### Amounts, and the one that bites

Beam takes **integer minor units** (`10000` = 100.00 THB). 2C2P takes a **decimal** (`1000.00`). Core stays
minor units (§3) and each adapter converts at its edge, with the 2C2P side formatting to exactly two
decimals under `InvariantCulture`. The old `TwoCTwoPService` passed `order.Amount` — a bare `decimal` from
a `decimal`-typed model — straight into the JWT payload, so this conversion never existed and a
culture-dependent format was one `CurrentCulture` away.

### Status mapping

| 2C2P `respCode` | Core |
| --- | --- |
| `0000` | `Succeeded` |
| `0001`, `2001` | `Pending` |
| `0003` | `Failed` + `Canceled` |
| `0004` (soft decline, retry after 3DS) | `Failed` + `AuthenticationFailed` |
| `2002` (not found) | throws `PaymentApiException(NotFound)` |
| `2003`, `0999` | `Failed` + `ProcessingFailed` |
| `4xxx` card codes (`4005` do not honor, `4051` insufficient funds, …) | `Failed` + `Declined` / `InsufficientFunds`, raw code always carried |

Card `4xxx` codes are mapped only where the meaning is unambiguous; everything else lands on `Unknown`
with its code intact, because a wrong normalization is worse than an honest `Unknown` (the old adapter
collapsed the lot into `IsSuccess = respCode == "0000"` plus a description string).

---

## 8. Deliberately not in v1

Cards, 3DS, card tokenization, CIT/MIT, installments, Beam Bolt devices, store links, transactions and
settlement reports. Cards drag PCI scope and a second flow (`skip3dsFlow`, authorization/capture) that no
consumer has asked for; the other three are Beam-only concepts with no second implementation in sight.

**What opsezy's choice of structure (3) adds, and it is all app domain:** the platform now issues the tax
invoice to the customer, withholds 3% when it pays the technician, and carries VAT on the gross. None of
that enters this package — but two existing Themia packages are on the path: document numbering for tax
invoices (`Themia.Framework.Data.Sequences`, whose gaps-are-acceptable semantic needs a cancelled-document
policy on top for Revenue Department purposes) and private storage for withholding certificates and the
technician's tax id (`StorageVisibility.Private` + presigned URLs, per the PDPA note above).

One thing the choice makes sharper: **refunds are now ours to make.** Under structure (1) a dissatisfied
customer was the technician's problem; under (3) the platform owes the refund, which makes §7's refund
semantics load-bearing — partial refunds exist only on card charges, so a QR PromptPay job refunds in full
or not at all.

Also out: **sub-merchant / split payment**. Structure (2) in §1 would add an "on behalf of" dimension to
every call — a sub-merchant id on `CreateChargeRequest`, a split instruction, and per-merchant onboarding
state — which is also the one thing that would justify `Themia.Modules.Payments`. Beam has partner mode
(`X-Beam-Partner-ID`) and 2C2P has `subMerchantList`, so both adapters could grow it. Not now: no consumer
is on that structure, and designing a split model against zero live merchants would be guesswork. ezy-assets
answered directly on coord #0146 (2026-09-25): a payout may grow out of their commission ledger eventually,
but not in their v1 or v2, and the blocker is the licensing structure rather than the engineering — so v1
here is not to be held for it.

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
- **2C2P:** a JWT round-trip against a fixed secret and a pinned token (sign → the documented payload,
  verify → the documented claims), `respCode` mapping across the table in §7b, decimal formatting under a
  comma-decimal `CurrentCulture` (the regression the old code could have had), and rejection of an
  `invoiceNo` over 20 characters **before** the call.
- **One test suite runs against both adapters** over the `IPaymentGateway` contract — create → read →
  refund with a recorded transport — so a change that fits only one provider fails.
- Live calls against Playground / sandbox-pgw are integration tests, skipped unless `THEMIA_BEAM_*` /
  `THEMIA_2C2P_*` env vars are set; CI runs the unit suite only.

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

- **Three packages from day one** (core + Beam + 2C2P), for the reasons in §2. A third provider later is a
  new adapter package and a DI line, not an app change.
- **2C2P ships as a port, not a rewrite of its flow.** Redirect API, hosted page, backend notification —
  the same flow ezy-assets ran. What changes is everything in §10.
- **No module, no store, no tenant scoping** while credentials are one account per app.
- **`Themia.PromptPay` has a plausible consumer under structure (1), but not an actual one yet.** That
  structure needs the customer to pay **the agent** directly, which means showing the agent's account or
  rendering the agent's PromptPay QR — and a gateway cannot do it, because a gateway charge settles to the
  merchant who created it. This package is the only thing in Themia that can produce such a QR. What is
  *not* true, and was briefly claimed here: that an app does this today. None does. opsezy's own payment
  spec (2026-09-15, written a week before counsel answered) renders a **Beam** dynamic PromptPay QR and has
  the customer paying Beam with the platform refunding — the shape counsel flagged. So the decision below
  stands on cost-to-keep, not on a live consumer, until an app actually adopts structure (1) in code.
- **A self-generated PromptPay QR is still not a collection path for the platform's own revenue.** It has
  no automatic confirmation: nothing tells the system the transfer happened, so every payment needs a human
  to read a slip or a bank statement. Commission and subscription charges therefore go through
  `IPaymentGateway`. And note whose problem confirmation is in structure (1): the money is the **agent's**,
  so a slip check there verifies a transfer into the agent's account, not ours. Beam's QR slip verification does **not** close that gap — it matches a slip
  against *one of your Beam charges* and flips that charge to `SUCCEEDED` (`404 NOT_FOUND_ERROR` when the
  slip matches nothing), so it confirms gateway charges and cannot confirm a QR Themia rendered offline.
  Automating a self-generated QR would need a bank feed or a third-party slip service — a new vendor, and
  out of scope. Collection therefore runs through `IPaymentGateway`; the direct QR stays available for
  printed or bill-payment QR codes that nobody waits on.
- **`Themia.PromptPay` does not move under this family and is not renamed.** It builds an EMVCo payload
  offline — no credentials, no charge, no status, no webhook — so it cannot implement `IPaymentGateway`, and
  a `Themia.Payments.PromptPay` name would promise a lifecycle it does not have. The QR-vs-gateway split is
  a settlement and trust decision the app makes; see `2026-09-22-themia-emvcoqr-design.md`.
- **Payment Links and slip verification stay on the Beam type**, not the interface, until a second provider
  offers the same thing.
