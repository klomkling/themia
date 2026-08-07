# Themia.PromptPay

PromptPay QR **payload** construction — EMVCo TLV assembly and CRC-16 for Credit Transfer (Tag 29) and
Bill Payment (Tag 30).

Pure computation: no HTTP, no credentials, no clock, no I/O. Targets `net8.0` and `net10.0`.

**Rendering the payload as a QR image is not included** — that pulls a drawing dependency into every
consumer, and the payload is the part that has to be right.

## Bill Payment (Tag 30)

The product a payment belongs to comes from the **registration**, not from the call:

```csharp
// composition root, once per product
var biller = BillerRegistration.PerProductSuffix(taxId: "0105500000000", suffix: "01");

// every call site
var payload = PromptPayQr.BillPayment(biller, reference: "INV-00042", amount: 590m);
```

When two products bill under one Tax ID, they must be distinguishable. Where they are distinguished
depends on what the bank issued:

| The bank issued | Use | The discriminator lives in |
|---|---|---|
| One suffix per product | `PerProductSuffix(taxId, suffix)` | the Biller ID |
| One suffix for both | `SharedSuffix(taxId, suffix, productPrefix)` | a prefix on Reference 1 |

`SharedSuffix` cannot be constructed without a product prefix, and this package prepends it — a call
site never does prefix arithmetic and never omits it. Switching between the two when the bank answers
is one line at composition, not a convention change audited across every call site.

> **Why the registration and not a parameter.** An earlier design took the biller id and suffix as
> separate required inputs and called that the fix for cross-product collisions. It protects only the
> first row of that table. In the second, both products pass the same suffix and the discriminator
> becomes a free-text prefix that nothing validates — the original silent failure, with a call site that
> now *looks* guarded. Where both products' payments land in one receiving account that string is the
> only signal attributing a payment: not a formatting convention with a safety net behind it, but the
> safety net.

### Reference length

`BillerRegistration.MaxReferenceLength` is **derived from the format, not guessed**. An EMVCo length
field is two decimal digits, so Tag 30's whole value is capped at 99 characters, of which the AID takes
20 and a 15-digit Biller ID takes 19:

```
20 (AID) + 19 (biller id) + 4 (Ref1 header) + len(Ref1)  <=  99   =>   len(Ref1) <= 56
```

56 characters, or 53 once a 3-character product prefix is reserved. Supplying a **Reference 2 lowers it
further** — that costs 4 characters plus its own length — and `BillPayment` checks the exact total, so an
over-long reference is refused here rather than by a bank later.

Your bank's own limit may be lower. It is not knowable from here, so this package does not invent one:
pass `maxReferenceLength` to tighten. It may only tighten, never widen.

## Credit Transfer (Tag 29)

```csharp
PromptPayQr.CreditTransfer(PromptPayProxy.MobileNumber("081-222-3333"), 590m);
PromptPayQr.CreditTransfer(PromptPayProxy.NationalId("1234567890123"));
PromptPayQr.CreditTransfer(PromptPayProxy.EWalletId("012345678901234"));
```

> **Tag 29 carries no reference fields.** A payment made against one arrives with nothing identifying
> what it was for, so reconciliation falls back to amount and timestamp — which stops working the moment
> two payers owe the same amount in the same window. Use Bill Payment when payments have to be matched.

Mobile numbers: formatting characters (spaces, hyphens, parentheses, a leading `+`) are stripped and
**nothing else is inferred**. What remains must be the 10-digit Thai national form or the 11-digit `66`
form. A number in another country's national format is rejected rather than reinterpreted as Thai —
guessing there does not fail, it succeeds, at whoever holds the resulting Thai number.

## Amounts

Omit the amount for a reusable QR the payer types into (point of initiation `11`); pass one for a
one-time QR with the amount fixed (`12`). Amounts always render with two decimals.

## Out of scope, permanently

Invoices, billing documents, reconciliation, running numbers, withholding tax, 50-Tawi certificates, and
the decision of **what** a reference should contain. This package accepts a reference and a registration
and constructs a correct payload; it does not decide what they mean.

Slip verification lives elsewhere — a service that only renders a QR must not depend on a verification
client it has no credentials for.

## Wire format

Pinned by golden vectors reproduced from an independent implementation and verified before this package
existed, by recomputing every checksum with a bitwise CRC written from the algorithm rather than from a
borrowed lookup table.

- CRC-16/CCITT-FALSE, polynomial `0x1021`, initial value `0xFFFF`, over the payload **including** the
  `6304` that introduces the checksum tag, emitted as four uppercase hex digits.
- Tag 29 AID `A000000677010111`; Tag 30 AID `A000000677010112`.
- Root tags in the order `00`, `01`, `29`/`30`, `53`, `58`, `54`. EMVCo requires only that `00` comes
  first and the checksum last, but the checksum covers the whole string, so the order is fixed and the
  golden vectors break if it changes.
