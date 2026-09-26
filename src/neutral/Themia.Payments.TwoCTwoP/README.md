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
start, registers the named `HttpClient`, and calls `AddThemiaPayments()` itself.

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

## Refunds are not supported in this version

```csharp
Task<RefundCreation> RefundAsync(RefundRequest request, CancellationToken cancellationToken = default) =>
    throw new PaymentApiException(FailureKind.Validation, "refund_not_supported", httpStatus: 0, /* … */);
```

`RefundAsync` **always** throws — nothing is sent to 2C2P. 2C2P's own refund path is the Payment
Maintenance API, which needs the merchant's RSA key pair plus 2C2P's certificate (a JWE wrapped inside a
JWS, `PS256`) rather than the shared HS256 secret every other call here uses, and only for a settled
transaction. Until that key pair exists for a real merchant, **refund from the 2C2P merchant portal.**

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
2. **The `THQR` group is not only PromptPay.** The group 2C2P calls `THQR` also carries TrueMoney,
   ShopeePay and GrabPay TH QR codes, not exclusively Thai QR PromptPay — a channel restriction meant to
   allow "PromptPay only" may admit more than that.
3. **The group codes themselves** (`CC` / `THQR` / `DEEPLINK` / `EWALLET`) — taken from 2C2P's published
   payment-channel reference, not from the older ezy-assets integration this package replaces, which used
   a different, non-matching set (`CC`/`THQR`/`EWALLETS`/`123`) and never actually sent the field.
