# Themia.Payments.TwoCTwoP

The [2C2P PGW 4.3](https://developer.2c2p.com/) (Redirect API) adapter for `Themia.Payments`:
`TwoCTwoPPaymentGateway` implements `IPaymentGateway` and `IPaymentGatewayCapabilities`, and
`TwoCTwoPWebhookVerifier` implements `IPaymentWebhookVerifier` for 2C2P's backend notification. Targets
`net8.0` and `net10.0`.

## Registration

```csharp
services.AddThemiaPaymentsTwoCTwoP(o =>
{
    o.MerchantId  = configuration["Payments:TwoCTwoP:MerchantId"]!;
    o.SecretKey   = configuration["Payments:TwoCTwoP:SecretKey"]!;
    o.Environment = TwoCTwoPEnvironment.Sandbox; // same API, no real money
    // Leave empty to mean "every channel"; set it to the channels this merchant is actually enabled
    // for with 2C2P, so a request for one you are not enabled for fails locally, not at 2C2P.
    o.PaymentChannels = [PaymentMethod.Card, PaymentMethod.QrPromptPay];
});
```

`AddThemiaPaymentsTwoCTwoP` validates `MerchantId`, `SecretKey`, `Timeout` and `TransactionTimeOffset` on
start, registers the named `HttpClient`, and calls `AddThemiaPayments()` itself. Register only one
payment adapter per container (see the `Themia.Payments` README).

Every method makes a single HTTP call, retried up to 3 attempts: with the default 30 s `Timeout` that is
up to ~91 s — pass a `CancellationToken` with your own deadline. A non-2xx response with no 2C2P body at
all (an HTML error page from a proxy) throws `PaymentApiException` with `ProviderCode` `HTTP_{status}`,
classified by status (`5xx` Transient, `429` RateLimited, `401` Authentication, `403` Permission, `404`
NotFound).

## The JWT signature *is* the authentication

Every request and response body — including the backend notification — is `{"payload": "<JWT>"}`,
HS256-signed with the shared merchant secret. There is no bearer token, no basic auth header, and no
separate signature field: **the JWT's own HS256 signature is 2C2P's entire authentication scheme** for
every direction of every call. A response is verified before anything in it is trusted; a body whose
`payload` fails verification is never parsed for its claims, only for the plain, unsigned
`{ "respCode", "respDesc" }` error shape 2C2P uses for request-level rejections (bad merchant, bad
signature).

## `ReferenceId` is 2C2P's `invoiceNo` — 20 characters, no more

```csharp
public required string ReferenceId { get; init; } // -> invoiceNo
```

2C2P caps `invoiceNo` at 20 characters. `CreateChargeAsync` checks this **before** calling 2C2P and
refuses locally (`PaymentApiException(FailureKind.Validation, "reference_id_too_long", …)`) rather than
letting a 21-character reference id fail at the provider.

## 2C2P mints no charge id before payment

`ChargeCreation.ChargeId` and `Charge.ChargeId` are always the caller's own `ReferenceId` — there is no
separate provider-issued id to track. `GetChargeAsync` (2C2P's Payment Inquiry) reads by `invoiceNo`
first; a `ChargeRef.ProviderChargeId` is accepted only as a fallback, and only because for this provider
invoice number and charge id are the same value.

## `respCode` mapping — `0999` is not a failed payment

| 2C2P `respCode` | Inquiry (`GetChargeAsync`) | Backend notification |
| --- | --- | --- |
| `0000` | `Succeeded` | `ChargeSucceeded` |
| `0001`, `2001` | `Pending` | `Other`, status `Pending` |
| `0003` / `0004` / `4005` / `4051` | `Failed` + `Canceled` / `AuthenticationFailed` / `Declined` / `InsufficientFunds` | `ChargeFailed` |
| `2002` | throws `PaymentApiException(NotFound)` | `ChargeFailed` |
| `0999`, `2003` and any other code | throws `PaymentApiException(Transient)`, raw code as `ProviderCode` | `Other`, status `Pending` — never `ChargeFailed` |

`0999` is 2C2P's own system error, `2003` ("Payment / Inquiry Failed") may mean only that the inquiry failed, and an unrecognised code says nothing reliable about whether the
shopper paid: reporting either as a failed payment could get a paid order cancelled. Retry the inquiry
instead. The raw code is always carried.

## Refunds

> **Not yet verified against the 2C2P sandbox.** The request and response shapes follow 2C2P's published
> Payment Maintenance documentation and are tested against key pairs generated in the tests only. Run a
> sandbox refund with your own merchant key pair before relying on it in production.

Refunds go through 2C2P's Payment Maintenance API (`processType` `R`), which does not use the shared secret
every other call here uses. The XML request is encrypted to 2C2P's public certificate as a JWE
(`RSA-OAEP` + `A256GCM`) and the JWE is signed with the merchant's private key as a JWS (`PS256`). Responses
are verified with 2C2P's public key first and only then decrypted; the algorithms are pinned, and a header
that names another algorithm or selects a key (`kid`, `jwk`, `jku`, `x5u`, `zip`, `crit`) is refused.

```csharp
services.AddThemiaPaymentsTwoCTwoP(o =>
{
    // ... MerchantId, SecretKey, Environment as above ...
    o.MerchantPrivateKeyPem       = configuration["Payments:TwoCTwoP:MerchantPrivateKeyPem"];       // RSA >= 2048, PKCS#8 or PKCS#1
    o.ProviderPublicCertificatePem = configuration["Payments:TwoCTwoP:ProviderPublicCertificatePem"]; // x509 from the 2C2P portal
});
```

Both are read from configuration, never logged, and checked on start (RSA of at least 2048 bits, an unexpired
certificate). Set both or neither: with neither, `RefundAsync` refuses with `refund_not_supported` and
sends nothing, as in earlier versions.

- **Full or partial.** `RefundRequest.Amount` of `null` refunds the charge's whole amount; otherwise the amount
  must be greater than zero, in the charge's currency, and not above the charge's amount. 2C2P refunds a
  settled transaction only.
- **Sent once, never retried.** Nothing establishes that 2C2P replays an `idempotencyID`, so a timeout, a transport
  failure, a `5xx`/`429`/`408`, an unverifiable response or an unknown `respCode` throws `PaymentApiException`
  `refund_outcome_unknown` (`FailureKind.Unknown`). **Check the 2C2P merchant portal before refunding again.**
  Cancelling the `CancellationToken` after the request is sent is reported the same way, not as a cancellation.
  The `IdempotencyKey` you pass is sent as `idempotencyID`; without one a fresh id is generated, which identifies
  nothing on a later attempt, so **pass a stable key per refund** (for example your refund record's id). A response
  that echoes another `idempotencyID` is refused. The named `HttpClient` has no resilience or retry handler; do not
  add one to it, it would re-send a refund.
- **Status.** `respCode` `00` with refund status `RF` is `Succeeded`; `RFF` (Refund Failed), `RR` (Refund Rejected) and `RR1` to `RR3` (rejected: insufficient balance, invalid bank information, bank account mismatch) are `Failed`, per 2C2P's [Payment Maintenance status codes](https://developer.2c2p.com/docs/response-code-payment-maintenance-status-code);
  `RP` and any status this package does not know are `Pending`, never `Succeeded`. `RefundCreation.RefundId` is
  always `null`: key your records on the charge. There is no refund webhook: 2C2P sends no notification when a
  refund settles, so follow a `Pending` refund with `GetRefundsAsync` (below) or the 2C2P merchant portal. A caller must never retry
  `refund_outcome_unknown` automatically.
- **2C2P is the only guard against over-refunding.** The inquiry does not show what was already refunded, so
  two concurrent or repeated partial refunds can exceed the charge locally unnoticed. Serialize refunds per charge.
- A response is accepted only when its `invoiceNo` (and `merchantID`, when present) match the request.

### Following a refund: `GetRefundsAsync`

`TwoCTwoPPaymentGateway` also implements `IRefundStatusGateway`. `GetRefundsAsync(ChargeRef)` asks the Payment Maintenance API for the refund status of the charge's invoice (`processType` `RS`) and returns a `RefundRecord` per refund: 2C2P's `referenceNo` as `RefundId`, the amount in the charge's currency, the status (mapped as above), the time, and `IdempotencyKey`.

- **Matching a record to the refund you sent.** The `IdempotencyKey` you pass to `RefundAsync` is also sent as the refund's `userDefined1`, and 2C2P returns it on the refund in the status list, so match on `RefundRecord.IdempotencyKey`. A refund made in the 2C2P portal (or one that 2C2P does not echo) has `null`. The key is sent as `userDefined1` only when it is at most 50 characters (2C2P publishes 150 for `userDefined1` on a payment token request but no length for the Payment Process request, and a rejected value would fail the refund); a longer key is still sent as `idempotencyID` but its record comes back with `IdempotencyKey` `null`, so match it on amount and time. Keep keys short.
- Needs the same keys as `RefundAsync` (`refund_not_supported` without them). It changes nothing and is safe to repeat: a failure to reach 2C2P is `FailureKind.Transient` (`refund_status_unavailable`), not `refund_outcome_unknown`. An unverifiable response, another invoice's, or a refund row with an unreadable amount is `malformed_response`; any other `respCode` is thrown as that code.
- Not verified against the 2C2P sandbox: the request omits `actionAmount` (the sample in the docs includes one for `RS`; the older documentation does not), and the `userDefined1` echo is as documented.

## What the shopper paid with: `Charge.PaidMethod`

`GetChargeAsync` fills `Charge.PaidMethod` from the inquiry's `channelCode` / `agentCode`, so a receipt can say Card rather than Transfer: `CC`, `CSTOKEN` and `VI` are `Card`; `THQR` with `PPQR` is `QrPromptPay`; `THQR` with `TRUEMONEYQR`, `SHPQR` or `GPTHQR` is `Wallet`. Anything else, including internet banking, is `null` (not paid yet, or a channel this adapter does not map), so keep a fallback. It labels a receipt; it never decides success. The codes come from 2C2P's payment-channel reference and inquiry sample and are **not verified against the sandbox** (the reference lists card as `CC` while the sample shows the scheme code `VI`; both are mapped).

## In-page PromptPay QR

A charge whose `AllowedMethods` is exactly `[PaymentMethod.QrPromptPay]` and whose `PresentationMode` is not `HostedPage` returns `NextAction.ShowQr` rather than the hosted-page `Redirect`, so the QR can be drawn inside your own page (for example in LINE LIFF). After the `paymentToken` call the gateway calls **Do Payment** (`POST /payment/4.3/payment`) once with `channelCode: THQR`, `agentCode: PPQR`, and expects flow code `1005` ("Pending for user scan QR") with `type: URL` and `data` = the QR PNG address, plus `expiryTimer` in milliseconds. `ShowQr.ImagePng` is that PNG, `RawPayload` is `null`, and `Expiry` is now + `expiryTimer` (or the request's `ExpiresAt` when 2C2P sends none).

- **`PresentationMode.HostedPage` opts out.** Set it to get the hosted-page `Redirect` for a PromptPay-only charge; Do Payment is then not called at all.
- **Always a safe fallback.** If Do Payment fails or answers anything else (another flow code, `type: QR` raw data, an error), or the image cannot be fetched, the charge still succeeds and returns the hosted-page `Redirect`. Check `Action` by type; do not assume `ShowQr`.
- **Image fetch is restricted:** `https` on a `2c2p.com` host only, no other host after redirects, at most 256 KB, and it must be a PNG.
- Do Payment is sent once and never retried. The shopper's payment still arrives through the normal notification / inquiry path; nothing about status mapping changes.
- Charges that allow cards, mobile banking or wallets keep the `Redirect`, since those need 2C2P's hosted page (3-D Secure, app switching).
- **Not yet verified against the 2C2P sandbox.** The request and response shapes come from 2C2P's "Do Payment" and "Payment Flow Codes" documentation; 2C2P's current docs show Do Payment at `/payment/4.5/payment`, while this package pins 4.3 like the rest of its calls. Run a sandbox charge before relying on it.

## Payment channels: group codes, not individual banks/wallets

`PaymentMethod` is sent to 2C2P as one of its `paymentChannel[]` group codes:

| `PaymentMethod` | 2C2P group code |
| --- | --- |
| `Card` | `CC` |
| `QrPromptPay` | `THQR` |
| `MobileBanking` | `DEEPLINK` |
| `Wallet` | `EWALLET` |

`TwoCTwoPOptions.PaymentChannels` narrows `SupportedMethods` (and, through it, what
`PaymentMethodGate`/`CreateChargeAsync` will accept) to the channels this merchant is actually enabled
for with 2C2P; left empty, `SupportedMethods` reads as "not restricted" (all four) rather than "none" —
2C2P has no capability-discovery call to ask instead.

## Verify in the 2C2P sandbox before going live

Three things this adapter takes on faith because 2C2P's documentation does not state them plainly.
Confirm each against a real sandbox transaction before trusting it in production:

1. **`TransactionTimeOffset` (default `+07:00`).** 2C2P's `transactionDateTime` is a bare
   `yyyyMMddHHmmss` string with no timezone, and 2C2P does not document which zone it is in. The default
   assumes Thailand; if your merchant account settles elsewhere, set
   `TwoCTwoPOptions.TransactionTimeOffset` (validated to within ±14 hours) to match.
   The same offset converts `CreateChargeRequest.ExpiresAt` into 2C2P's bare `paymentExpiry`
   (`yyyy-MM-dd HH:mm:ss`); without an `ExpiresAt`, 2C2P's own default of 20 minutes applies.
2. **The `THQR` group is not only PromptPay.** The group 2C2P calls `THQR` also carries TrueMoney,
   ShopeePay and GrabPay TH QR codes, not exclusively Thai QR PromptPay — a channel restriction meant to
   allow "PromptPay only" may admit more than that.
3. **The group codes themselves** (`CC` / `THQR` / `DEEPLINK` / `EWALLET`) — taken from 2C2P's published
   payment-channel reference, not from the older ezy-assets integration this package replaces, which used
   a different, non-matching set (`CC`/`THQR`/`EWALLETS`/`123`) and never actually sent the field.
