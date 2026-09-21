# Themia.EmvcoQr — design

**Status:** deferred. Written now so the split is decided before something forces it, **not** scheduled.
**Build trigger:** either (a) a second country's QR scheme is needed (QRIS, DuitNow, PayNow, VietQR, KHQR),
or (b) a consumer needs to **read** an EMVCo payload rather than write one — see §3, which is the likelier
of the two and already has a caller in sight.
**Target version:** none assigned. Do not implement on this document alone; re-read §7 first.
**Related:** `2026-09-22-themia-payments-design.md` (§11 decides PromptPay does not move under
`Themia.Payments`), `Themia.PromptPay` README ("Out of scope, permanently").

---

## 1. What this would be

`Themia.PromptPay` today is 472 lines across three files and holds two different things:

| Concern | Thai? | Files |
| --- | --- | --- |
| EMVCo TLV assembly, CRC-16/CCITT-FALSE, payload-format / point-of-initiation / currency / country tags | **No** — EMVCo Merchant-Presented Mode, used by QRIS, DuitNow, PayNow, QR Ph, VietQR, KHQR alike | inside `PromptPayQr.cs` |
| AIDs `A000000677010111` (Tag 29, Credit Transfer) and `A000000677010112` (Tag 30, Bill Payment), proxy types (national/tax id, mobile, e-wallet), biller registration + suffix rules, Thai reference limits | **Yes** — ITMX, Thai banks only | `PromptPayQr.cs` constants, `PromptPayProxy.cs`, `BillerRegistration.cs` |

The split puts the first in its own package:

```
Themia.EmvcoQr             net8.0;net10.0   TLV writer + reader, CRC-16/CCITT-FALSE, common tag ids
Themia.PromptPay           net8.0;net10.0   Thai AIDs, proxies, biller registration  (unchanged package id)
Themia.EmvcoQr.Qris / …    net8.0;net10.0   a second country, if one ever arrives
```

PromptPay is a **Thailand-only scheme**. What travels is the format, not the scheme: a Thai merchant's QR
is payable from foreign wallets through ASEAN cross-border linkages, but no other country runs PromptPay.
So the reusable seam is the payload format — which is exactly why the family name is `EmvcoQr` and not
`Payments`.

---

## 2. Naming — and why `Themia.PromptPay` keeps its id

The obvious symmetry is `Themia.EmvcoQr.PromptPay`. It is rejected as the default:

- `Themia.PromptPay` is **published** (0.14.0, coord #0055) and consumed. Renaming costs every
  consumer a `PackageReference` change, a namespace change, and a `PublicAPI` reset — for a tidier name.
- Its 18-line public surface (`PromptPayQr`, `PromptPayProxy`, `BillerRegistration`) does not change under
  the split; only the internals move. A rename would be churn with no API benefit.

**Decision:** keep `Themia.PromptPay` as the package id and let it depend on `Themia.EmvcoQr`. If the
family name is wanted later, do it in the same release as another breaking rename, publish
`Themia.EmvcoQr.PromptPay` as the new home and keep `Themia.PromptPay` for one release as a shim of
`[assembly: TypeForwardedTo]` — a rename executed without type forwarding breaks consumers silently at
runtime rather than at compile time.

---

## 3. The read direction — the trigger most likely to fire first

`Themia.PromptPay` only writes payloads. Two live needs read them:

- **Beam slip verification** takes `format=RAW` with "the content of the QR code from the bank transfer
  slip". An app that scans the slip itself holds an EMVCo payload and today has nowhere in Themia to parse
  or sanity-check it before spending an API call.
- A **received** PromptPay QR (a payer-presented or merchant-presented code from elsewhere) can be checked
  for CRC validity and inspected for proxy/amount before use.

A reader is genuinely shared: the TLV walk and CRC check are scheme-independent, and the Thai AID lookup on
top is `Themia.PromptPay`'s. A reader is **not** a validator of whether money moved — that is slip
verification, which is Beam's (see the payments spec §7).

---

## 4. Surface, if built

```csharp
// Themia.EmvcoQr
public readonly record struct EmvcoTag(string Id, string Value);
public static class EmvcoPayload
{
    public static string Build(IReadOnlyList<EmvcoTag> tags);          // appends 6304 + CRC
    public static bool TryParse(string payload, out EmvcoDocument doc); // CRC-checked
}
public static class Crc16Ccitt { public static ushort Compute(ReadOnlySpan<byte> data); }
public static class EmvcoTagIds { public const string PayloadFormatIndicator = "00"; /* 01, 53, 54, 58, 59, 60, 62, 63 */ }
```

Rules carried over verbatim from the current implementation and its golden vectors — they are the whole
correctness of this package:

- CRC-16/CCITT-FALSE, polynomial `0x1021`, initial value `0xFFFF`, computed **including** the `6304` that
  introduces the checksum, emitted as four uppercase hex digits.
- Point of initiation `11` (reusable, no amount) vs `12` (one-time, amount fixed); amounts always two
  decimals; currency `764`; country `TH` stays on the Thai side.
- Nested TLV inside the merchant-account tag.

`Themia.PromptPay` keeps `PromptPayQr.CreditTransfer` / `.BillPayment`, the two AID constants, the proxy
factories and `BillerRegistration` exactly as they are, and builds its tag list through `EmvcoPayload`.

---

## 5. What this is not

Not QR **image** rendering — the current package's description says image rendering is the application's
choice, and that stays true. Not slip verification. Not a payment gateway: nothing here knows whether a QR
was ever paid, which is precisely why `Themia.Payments` and this family are separate (payments spec §11).

---

## 6. Testing

The existing golden vectors move with the code and must pass **byte-identical** before and after the split;
that is the acceptance criterion for the refactor, and no new vector is needed to prove it. The reader adds:
round-trip (`Build` → `TryParse` → same tags), a corrupted-CRC rejection, a truncated-length rejection, and
a payload with an unknown tag preserved rather than dropped.

---

## 7. Why this is deferred, and what would change that

Today there is **one** scheme, and the TLV/CRC code is ~60 lines inside one file with no second caller.
Splitting now produces a package whose only consumer is the package it was extracted from — the "abstraction
with one implementation" shape this repo has argued against elsewhere (`Themia.Geo` §2 refused a module for
the same reason).

Extraction stays cheap for exactly as long as the code stays where it is: it is private, it is covered by
golden vectors, and no consumer references it. So the correct move is to **wait for the trigger** in the
header — a second scheme, or the first real reader caller — and then extract in one commit.

What must *not* happen in the meantime: a second country's tag assembly being written inside
`Themia.PromptPay` "for now". That is the state this document exists to prevent.
