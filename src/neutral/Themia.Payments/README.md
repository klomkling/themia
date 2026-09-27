# Themia.Payments

A provider-agnostic payment seam: `IPaymentGateway` (create a charge, read it, refund it),
`IPaymentWebhookVerifier` (check a webhook's signature and read it), and a `PaymentMethodPolicy` an
adopter writes to say which methods an amount may be paid with. No HTTP, no credentials, no adapter —
a provider package (`Themia.Payments.Beam`, `Themia.Payments.TwoCTwoP`) implements the seam. Targets
`net8.0` and `net10.0`.

Every charge created through this seam must be the platform's **own revenue**. Collecting money on
behalf of someone else and settling it later is a licensed payment business under Thailand's Payment
Systems Act, so that flow is out of scope by law, not preference.

## No `Themia.Modules.Payments`

Nothing here is tenant-scoped or persisted — no schema, no `IThemiaModule` — so there is no module, the
same call as `Themia.Geo` and `Themia.AI`. The one thing that would justify one, sub-merchant split
payment, is out of scope: no adopter needs it, and it is the licensed flow above.

## The smallest working registration

```csharp
services.AddThemiaPaymentsBeam(o =>
{
    o.MerchantId = configuration["Payments:Beam:MerchantId"]!;
    o.ApiKey     = configuration["Payments:Beam:ApiKey"]!;
});

services.AddThemiaPayments(); // idempotent — AddThemiaPaymentsBeam already calls it
```

`AddThemiaPayments` registers `ThemiaPaymentsOptions` (`ValidateOnStart`) and `PaymentMethodGate`; every
adapter's own `Add…` calls it too, with `TryAdd`/`TryAddEnumerable` throughout, so registering both is
never a duplicate and the order between them does not matter.

**One adapter per container.** Each adapter registers `IPaymentGateway`, `IPaymentGatewayCapabilities`
and `IPaymentWebhookVerifier` with `TryAdd`, so registering both `AddThemiaPaymentsBeam` and
`AddThemiaPaymentsTwoCTwoP` in one container does not fail — the **first** one silently keeps all three
seams and the second only adds its own concrete types. Pick one provider per container.

## Failures, retries and the time budget

Every adapter retries a transient failure (`5xx`, `429`, a transport error, or its own per-request
timeout) up to **3 attempts** per HTTP call, with exponential backoff plus jitter, and never retries a
`4xx`. With the default 30-second `Timeout`, one HTTP call can therefore take **about 91 seconds** in the
worst case, and a method that chains several calls multiplies that (each adapter's README lists its
chains). **Pass a `CancellationToken` carrying your own deadline**; your cancellation propagates as
`OperationCanceledException` and is never retried or wrapped.

When the retries are exhausted at the transport, the call throws
`PaymentApiException(FailureKind.Transient, "transport_error" | "timeout", httpStatus: 0)` with the
underlying exception as `InnerException` — never a raw `HttpRequestException` or `TaskCanceledException`.
A 2xx body that does not hold what the adapter needs is
`PaymentApiException(FailureKind.Unknown, "malformed_response", status)`.

A caller-supplied `IdempotencyKey` (on a charge, a refund, or a Beam payment link) must be non-blank, at
most 255 characters of visible ASCII (no spaces or control characters), or it is refused locally as
`PaymentApiException(FailureKind.Validation, "idempotency_key_invalid", 0)`. A `ChargeRef` with neither
id set is an `ArgumentException` in every adapter.

## `PaymentStatus.Pending` can last for ever

```csharp
public enum PaymentStatus { Pending, Succeeded, Failed }
```

`Pending` is **not a transient state you may wait on**. A shopper who closes the QR screen, or never
opens the payment link, leaves the charge `Pending` indefinitely — there is no "it will resolve shortly"
guarantee, and nothing here ships a wait-until-final helper, however convenient one looks. Give every
unresolved charge your own timeout, treat the timeout as unpaid, and keep the charge id: a very late
success is still possible and must still be recognised.

## A verified webhook proves origin, not freshness

```csharp
WebhookVerification Verify(ReadOnlySpan<byte> rawBody, IReadOnlyDictionary<string, string> headers);
```

`WebhookOutcome.Verified` means the signature matched — it does not mean the request is new. Beam's
signature covers the body with no timestamp and no nonce, so a captured, genuine request replays
correctly for ever. **Deduplicate on your own key** (charge id + status is enough); this package cannot
do it for you because it has no storage. Call `Verify` with the **exact bytes received** — an ASP.NET
Core host must `EnableBuffering()` and read the body before model binding, since a provider signs the
bytes it sent, not whatever a round-trip through `System.Text.Json` reproduces.

## Partial refunds are not universal

`RefundRequest.Amount` is nullable — `null` means "refund the maximum refundable amount" — and a partial
amount is refused for methods whose provider does not support one at all (Beam, for example, only
partially refunds a `CARD` charge; a QR PromptPay charge refunds in full or not at all). An adapter
reports this as `PaymentApiException(FailureKind.Validation, …)`, not a silent full refund.

## The method policy: fee, not preference

```csharp
services.AddThemiaPayments(o => o.MethodPolicy = new PaymentMethodPolicy
{
    Currency = "THB",
    // At or below 1,000.00 THB, only QR — a card's percentage fee can be a tenth of a small charge.
    Bands = [new PaymentMethodBand(UpToMinorUnitsInclusive: 100_000, Methods: [PaymentMethod.QrPromptPay])],
    // Above every band, QR or card.
    Above = [PaymentMethod.QrPromptPay, PaymentMethod.Card],
});
```

`PaymentMethodGate.Apply(amount, requested)` — which every adapter calls before building a payload —
**intersects** the policy with the caller's own list rather than silently substituting a cheaper method:
a caller that asked for one method usually has a reason (a saved card, a retry of a failed attempt), so
an empty intersection is a `PaymentApiException` naming both lists. `MethodPolicy` is validated at
startup against the registered adapter's `IPaymentGatewayCapabilities` — a policy that names a method no
adapter supports fails to boot rather than failing the first time a charge actually needs that band. This
does not pick the cheapest method, reorder anything, or model fees itself: rates are per-merchant
contract terms that change without a release, so this package only gives you the switch.

## `Money` is minor units, not `decimal`

Every provider in this space is integer-minor-unit on the wire (Beam takes `10000` for 100.00 THB), and a
`decimal` puts both the rounding and the currency in the caller's head at each call site.

```csharp
Money.Thb(10_000);                 // 100.00 THB
Money.From(minorUnits: 500, "USD"); // any ISO 4217 currency
```

## Adapters ship separately

Only the seam lives here. `Themia.Payments.Beam` and `Themia.Payments.TwoCTwoP` each register their own
`IPaymentGateway`, `IPaymentGatewayCapabilities` and `IPaymentWebhookVerifier` — read their own READMEs
for what each provider actually does and does not support.
