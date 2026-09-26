# Themia.Payments.Beam

The [Beam Checkout](https://docs.beamcheckout.com/) adapter for `Themia.Payments`: `BeamPaymentGateway`
implements `IPaymentGateway` and `IPaymentGatewayCapabilities`, `BeamWebhookVerifier` implements
`IPaymentWebhookVerifier`, and `BeamPaymentClient` exposes the Beam-only features — payment links and QR
slip verification — that have no counterpart on the provider-agnostic interfaces. Targets `net8.0` and
`net10.0`.

## Registration

```csharp
services.AddThemiaPaymentsBeam(o =>
{
    o.MerchantId      = configuration["Payments:Beam:MerchantId"]!;
    o.ApiKey          = configuration["Payments:Beam:ApiKey"]!;
    o.Environment     = BeamEnvironment.Playground; // sandbox; same API, no real money
    o.WebhookHmacKey  = configuration["Payments:Beam:WebhookHmacKey"]; // needed only to verify webhooks
});
```

`AddThemiaPaymentsBeam` validates `MerchantId`, `ApiKey` and `Timeout` on start, registers the named
`HttpClient`, and calls `AddThemiaPayments()` itself — a host does not need to call both.

## Only a lone `QrPromptPay` charge is a direct charge

`CreateChargeAsync` posts a direct `POST /api/v1/charges` **only** when `AllowedMethods` resolves (after
the method policy) to exactly `[QrPromptPay]`. Everything else — a lone `Card`, `MobileBanking`,
`Wallet`, or more than one method offered together — creates a **Beam payment link**
(`POST /api/v1/payment-links`) instead: Beam's hosted checkout page collects the details and lets the
shopper choose.

This is not the same failure mode as "no direct card charge type" — a direct `POST /api/v1/charges` needs
card data or a saved card token, and card tokenization is out of scope for this version (spec §8), so a
lone `[Card]` request could never succeed as a direct charge anyway. Routing it through a link, where
Beam's own hosted page collects the card, is the only way a card payment can complete at all today.

**When a request becomes a link, `ChargeCreation.ChargeId` is the *link's* id, not a charge id.**
`GetChargeAsync` knows this: given that id, it first tries `GET /api/v1/charges/{id}` (a genuine charge
id), and on a 404 retries it as `GET /api/v1/payment-links/{id}`, following a **paid** link to the charge
that actually paid it. A charge id you got back from a single-method QR charge is a real charge id from
the start; a charge id from any other request is a link id until someone pays it.

```csharp
var creation = await gateway.CreateChargeAsync(new CreateChargeRequest
{
    Amount = Money.Thb(50_000),
    ReferenceId = "order-42",
    AllowedMethods = [PaymentMethod.Card], // -> a payment link; creation.ChargeId is the link id
});

// Later, regardless of which path created it:
var charge = await gateway.GetChargeAsync(new ChargeRef(creation.ChargeId, "order-42"));
```

## `BeamPaymentClient` — Beam-only features

Reaching for `BeamPaymentClient` is how an app says out loud that it is now tied to Beam.

```csharp
var client = provider.GetRequiredService<BeamPaymentClient>();

var link = await client.CreatePaymentLinkAsync(new BeamPaymentLinkRequest
{
    Amount = Money.Thb(50_000),
    ReferenceId = "order-42",
    AllowedMethods = [PaymentMethod.Card, PaymentMethod.QrPromptPay],
    ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
});

await client.GetPaymentLinkAsync(link.Id);
await client.DisablePaymentLinkAsync(link.Id); // the only mutation — a link cannot be edited or deleted

var verification = await client.VerifyQrSlipAsync(rawQrContent); // or the Stream/fileName overload, PNG/JPEG ≤ 2 MB
```

`CreatePaymentLinkAsync` applies the same `CreateChargeRequestValidator` and `PaymentMethodGate` the
gateway does — this client is not a way around the adopter's method policy.

**Slip verification matches only this merchant's own Beam-created charges.** A slip printed for a
transfer Beam never charged — including a QR rendered offline by `Themia.PromptPay` — verifies as
`FailureKind.NotFound`, never a false match. The image overload refuses a filename containing a double
quote, a backslash, or a control character (it is interpolated into a `Content-Disposition` header), and
an image larger than 2 MB before it is ever sent.

## The webhook signature proves origin, not freshness

`X-Beam-Signature` is an HMAC-SHA256 over the exact raw body — it authenticates that Beam sent it, but
Beam's scheme carries **no timestamp and no nonce**. A captured, genuine webhook replays correctly for
ever. `BeamWebhookVerifier` cannot protect you from this by itself: **deduplicate on your own key**
(charge id + status is enough) before acting on a `Verified` event.

The event name arrives on the **unsigned** `X-Beam-Event` header — the HMAC covers only the body — so
the verifier reconciles a modelled event's claimed type against the signed body's own `chargeId` /
`refundId` / `status` before trusting it, and reports `WebhookOutcome.Malformed` rather than a
`PaymentEvent` the body does not actually support when they disagree.

```csharp
services.AddThemiaPaymentsBeam(o => { /* … */ o.WebhookHmacKey = configuration["Payments:Beam:WebhookHmacKey"]; });

// in the webhook endpoint, after EnableBuffering() and reading the raw body:
var result = verifier.Verify(rawBody, headers);
if (result.Outcome != WebhookOutcome.Verified) { return Results.BadRequest(); }
// dedupe on (result.Event!.ChargeId, result.Event.Status) before acting
```
