# Themia.Payments Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `Themia.Payments`, `Themia.Payments.Beam` and `Themia.Payments.TwoCTwoP` in `0.30.0` — one provider-agnostic seam for collecting a payment and learning its outcome, with two real adapters.

**Architecture:** The core holds value types, the three-operation gateway interface, a webhook verifier contract, and one optional rule (which methods an amount may be paid with). It has no HTTP and no storage. Each adapter owns its provider's transport, its payload shapes, and a mapping table from provider vocabulary to the core's — Beam over JSON with a Basic-auth header and an HMAC webhook signature, 2C2P over JWT-wrapped JSON where the signature *is* the authentication. Nothing persists: the app owns the order ↔ charge mapping, because only the app knows what the charge was for.

**Tech Stack:** .NET 8 + .NET 10, `Microsoft.Extensions.Http` 10.0.9, `Microsoft.Extensions.Options` 10.0.9, `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.9, `System.Text.Json` (framework), xUnit 2.9.3, Microsoft.NET.Test.Sdk 18.6.0.

**Spec:** `docs/superpowers/specs/2026-09-22-themia-payments-design.md` — read it before Task 1. Section numbers (§4, §4b, §6, §7, §7b) below refer to it.

## Global Constraints

Every task's requirements include this section.

**Packages and layering**
- Three packaged projects, all `net8.0;net10.0`, under `src/neutral/`: `Themia.Payments`, `Themia.Payments.Beam`, `Themia.Payments.TwoCTwoP`.
- `Themia.Payments` references `Microsoft.Extensions.Options` and `Microsoft.Extensions.DependencyInjection.Abstractions` only. **No HTTP, no driver, no `Themia.Framework.*`, no `Microsoft.AspNetCore.App`.**
- Each adapter references the core plus `Microsoft.Extensions.Http`. **Adapters never reference each other.**
- Every packaged project tracks `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` (both start with `#nullable enable`), carries XML docs on every public member, and builds under repo-wide `TreatWarningsAsErrors=true`.
- `System.Text.Json` only. `ILogger<T>` only.
- Each project adds `<InternalsVisibleTo Include="<ProjectName>.Tests" />`, as `Themia.Geo.Google` does.

**Money and amounts (§3)**
- `Money` is `(long MinorUnits, string Currency)`. `10000` is 100.00 THB. Currency is ISO 4217, three letters, uppercase, compared ordinally.
- Adapters convert at their edge: Beam sends minor units as-is; 2C2P sends a decimal formatted with exactly two decimals under `CultureInfo.InvariantCulture`.

**Security and logging**
- Never log the API key, the merchant secret, the webhook HMAC key, or a full request/response body. Log the charge id, the reference id, the provider code and the HTTP status.
- Webhook signatures are compared with `CryptographicOperations.FixedTimeEquals`, over raw bytes, never over a re-serialized object.

**Idempotency and retries (§5)**
- An idempotency key is built **once per logical operation**, above the retry loop, and reused on every attempt.
- Retry only `5xx`, timeouts and `429`, with exponential backoff plus jitter, capped at 3 attempts. **Never retry a 4xx.**

**Out of scope for this plan (§8)**
- Cards' 3DS, tokenization, CIT/MIT, installments, Beam Bolt, store links, transactions/settlement reports, sub-merchant split payment, and any ASP.NET Core endpoint package.

## Review Focus

These are the inputs most likely to bite someone using this, none of which the happy path exercises. Each has a test in the task that owns the code.

1. **A zero or negative charge amount** — `CreateChargeRequest` with `Money(0, "THB")` must be rejected before any HTTP call, not sent to the provider (Task 3).
2. **A currency the policy was not written for** — a THB band table asked to resolve a USD amount must throw, not silently apply baht thresholds (Task 4).
3. **A webhook body that was parsed and re-serialized before verification** — must fail as `SignatureMismatch` rather than pass, because whitespace and key order change the bytes (Task 10).
4. **A retry after a timeout on a charge that actually succeeded** — the second attempt must carry the same idempotency key, so the provider returns the first charge instead of creating a second (Task 8).
5. **A partial refund against a QR PromptPay charge** — rejected by the adapter with a reason before the call, because Beam allows partial refunds only on `CARD` (Task 9).

---

## File Structure

```
src/neutral/Themia.Payments/
  Money.cs                      Money value type + currency validation
  PaymentEnums.cs               PaymentStatus, FailureReason, PaymentMethod, PaymentEventType, WebhookOutcome, FailureKind
  PaymentContracts.cs           PaymentFailure, ChargeRef, Charge, NextAction, ChargeCreation, CreateChargeRequest, RefundRequest, RefundCreation
  IPaymentGateway.cs            the three operations + IPaymentGatewayCapabilities
  PaymentApiException.cs        transport/API failures
  Webhooks/IPaymentWebhookVerifier.cs, WebhookVerification.cs, PaymentEvent.cs
  MethodPolicy/PaymentMethodBand.cs, PaymentMethodPolicy.cs, PaymentMethodGate.cs, ThemiaPaymentsOptions.cs, PaymentMethodPolicyValidator.cs
  DependencyInjection/PaymentsServiceCollectionExtensions.cs
  README.md, PublicAPI.*.txt

src/neutral/Themia.Payments.Beam/
  BeamOptions.cs, BeamEnvironment.cs
  BeamPaymentGateway.cs         IPaymentGateway + IPaymentGatewayCapabilities
  BeamPaymentClient.cs          Beam-only surface: payment links, QR slip verification
  BeamMapping.cs                status / failure / action / method maps
  BeamWebhookVerifier.cs
  Internal/BeamHttp.cs          auth header, idempotency header, retry policy, error translation
  DependencyInjection/BeamServiceCollectionExtensions.cs
  README.md, PublicAPI.*.txt

src/neutral/Themia.Payments.TwoCTwoP/
  TwoCTwoPOptions.cs, TwoCTwoPEnvironment.cs
  TwoCTwoPPaymentGateway.cs
  TwoCTwoPMapping.cs            respCode map
  TwoCTwoPWebhookVerifier.cs    backend notification (JWT body)
  Internal/JwtHs256.cs          encode/decode/verify — no JWT library
  Internal/TwoCTwoPHttp.cs
  DependencyInjection/TwoCTwoPServiceCollectionExtensions.cs
  README.md, PublicAPI.*.txt

tests/Themia.Payments.Tests/                 core value types, policy, gate
tests/Themia.Payments.Beam.Tests/            adapter + golden webhook vector
tests/Themia.Payments.TwoCTwoP.Tests/        adapter + JWT vectors
tests/Themia.Payments.ContractTests/         one suite run against both adapters
```

---

### Task 1: Core project, `Money`, and the enums

**Files:**
- Create: `src/neutral/Themia.Payments/Themia.Payments.csproj`, `Money.cs`, `PaymentEnums.cs`, `PublicAPI.Shipped.txt`, `PublicAPI.Unshipped.txt`
- Create: `tests/Themia.Payments.Tests/Themia.Payments.Tests.csproj`, `MoneyTests.cs`
- Modify: `Themia.sln`

**Interfaces:**
- Produces: `Themia.Payments.Money` (`readonly record struct`, `long MinorUnits`, `string Currency`, `static Money Thb(long)`, `static Money From(long, string)`), and the enums `PaymentStatus`, `FailureReason`, `PaymentMethod`, `PaymentEventType`, `WebhookOutcome`, `FailureKind`.

- [ ] **Step 1: Create the two projects**

```bash
cd Packages/themia
dotnet new classlib -o src/neutral/Themia.Payments -f net10.0
dotnet new xunit -o tests/Themia.Payments.Tests -f net10.0
rm src/neutral/Themia.Payments/Class1.cs tests/Themia.Payments.Tests/UnitTest1.cs
```

Replace `src/neutral/Themia.Payments/Themia.Payments.csproj` with (copy the shape from `src/neutral/Themia.Geo/Themia.Geo.csproj`):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- Neutral cross-framework package: MUST include net8.0 (cross-framework reuse). -->
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <PackageId>Themia.Payments</PackageId>
    <Description>Provider-agnostic payment collection: create a charge, read its outcome, refund it, and verify a provider's webhook. No HTTP and no storage — adapters supply the transport.</Description>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Options" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.PublicApiAnalyzers">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <AdditionalFiles Include="PublicAPI.Shipped.txt" />
    <AdditionalFiles Include="PublicAPI.Unshipped.txt" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Themia.Payments.Tests" />
  </ItemGroup>
</Project>
```

Both `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` start as a single line: `#nullable enable`.

Test csproj: copy `tests/Themia.Geo.Tests/Themia.Geo.Tests.csproj` verbatim and change the `ProjectReference` to `../../src/neutral/Themia.Payments/Themia.Payments.csproj`.

```bash
dotnet sln Themia.sln add src/neutral/Themia.Payments --solution-folder neutral
dotnet sln Themia.sln add tests/Themia.Payments.Tests --solution-folder tests
```

- [ ] **Step 2: Write the failing tests**

`tests/Themia.Payments.Tests/MoneyTests.cs`:

```csharp
using Themia.Payments;
using Xunit;

namespace Themia.Payments.Tests;

public class MoneyTests
{
    [Fact]
    public void Thb_carries_minor_units_and_currency()
    {
        var money = Money.Thb(10000);

        Assert.Equal(10000, money.MinorUnits);
        Assert.Equal("THB", money.Currency);
    }

    [Fact]
    public void Currency_is_upper_cased_so_comparison_can_stay_ordinal()
    {
        Assert.Equal("USD", Money.From(500, "usd").Currency);
    }

    [Theory]
    [InlineData("TH")]
    [InlineData("THBB")]
    [InlineData("TH1")]
    [InlineData("")]
    public void A_currency_that_is_not_three_letters_is_refused(string currency)
    {
        Assert.ThrowsAny<ArgumentException>(() => Money.From(100, currency));
    }

    [Fact]
    public void A_negative_amount_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Money.Thb(-1));
    }

    [Fact]
    public void Zero_is_allowed_because_a_refund_of_zero_means_the_maximum_refundable_amount()
    {
        Assert.Equal(0, Money.Thb(0).MinorUnits);
    }
}
```

- [ ] **Step 3: Run the tests and watch them fail**

Run: `dotnet test tests/Themia.Payments.Tests`
Expected: FAIL — `Money` does not exist.

- [ ] **Step 4: Implement `Money` and the enums**

`src/neutral/Themia.Payments/Money.cs`:

```csharp
namespace Themia.Payments;

/// <summary>An amount in a currency's smallest unit.</summary>
/// <remarks>
/// Minor units, not <see cref="decimal"/>: every provider in this space is integer-minor-unit on the wire
/// (Beam takes <c>10000</c> for 100.00 THB), and a decimal puts both the rounding and the currency in the
/// caller's head at each call site. An adapter whose provider wants a decimal formats it at its own edge.
/// </remarks>
public readonly record struct Money
{
    private Money(long minorUnits, string currency)
    {
        MinorUnits = minorUnits;
        Currency = currency;
    }

    /// <summary>The amount, in the currency's smallest unit. Never negative.</summary>
    public long MinorUnits { get; }

    /// <summary>The ISO 4217 code, upper-cased.</summary>
    public string Currency { get; }

    /// <summary>An amount in Thai baht.</summary>
    /// <param name="minorUnits">Satang. <c>10000</c> is 100.00 THB.</param>
    public static Money Thb(long minorUnits) => From(minorUnits, "THB");

    /// <summary>An amount in any currency.</summary>
    /// <param name="minorUnits">The amount in the currency's smallest unit.</param>
    /// <param name="currency">An ISO 4217 code; case is normalised.</param>
    /// <exception cref="ArgumentException"><paramref name="currency"/> is not three ASCII letters.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minorUnits"/> is negative.</exception>
    public static Money From(long minorUnits, string currency)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minorUnits);
        ArgumentNullException.ThrowIfNull(currency);
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetter))
        {
            throw new ArgumentException($"'{currency}' is not an ISO 4217 code.", nameof(currency));
        }

        return new Money(minorUnits, currency.ToUpperInvariant());
    }
}
```

`src/neutral/Themia.Payments/PaymentEnums.cs` — the six enums, each member documented:

```csharp
namespace Themia.Payments;

/// <summary>Where a charge is in its life.</summary>
public enum PaymentStatus
{
    /// <summary>
    /// The outcome is not known. <b>This is not a transient state you may wait on:</b> a shopper who closes
    /// the QR screen leaves the charge here indefinitely. Give every unresolved charge your own timeout,
    /// treat the timeout as unpaid, and keep the charge id — a very late success is still possible.
    /// </summary>
    Pending,

    /// <summary>The payment succeeded. Final.</summary>
    Succeeded,

    /// <summary>The payment failed. Final; read the failure for why.</summary>
    Failed,
}

/// <summary>Why a payment failed, normalized across providers.</summary>
public enum FailureReason
{
    /// <summary>The provider's code has no mapping here. Read the raw code.</summary>
    Unknown,

    /// <summary>The provider could not process the payment.</summary>
    ProcessingFailed,

    /// <summary>The payer did not have the funds.</summary>
    InsufficientFunds,

    /// <summary>The payer could not be authenticated.</summary>
    AuthenticationFailed,

    /// <summary>The issuer declined.</summary>
    Declined,

    /// <summary>The payment window closed before it was paid.</summary>
    Expired,

    /// <summary>The payment was cancelled. 2C2P reports this as <c>0003</c>; Beam has no equivalent.</summary>
    Canceled,
}

/// <summary>A way to pay, at the granularity where cost differs.</summary>
/// <remarks>
/// Finer distinctions stay provider-side: Beam's KPLUS / SCB_EASY / KRUNGSRI_APP / BANGKOK_BANK_APP are all
/// <see cref="MobileBanking"/>, and TRUE_MONEY / LINE_PAY / SHOPEE_PAY / ALIPAY are all <see cref="Wallet"/>.
/// An adapter declares which of these it supports through <see cref="IPaymentGatewayCapabilities"/>.
/// </remarks>
public enum PaymentMethod
{
    /// <summary>A Thai QR PromptPay transfer.</summary>
    QrPromptPay,

    /// <summary>A credit or debit card.</summary>
    Card,

    /// <summary>A bank's own mobile app.</summary>
    MobileBanking,

    /// <summary>An e-wallet.</summary>
    Wallet,
}

/// <summary>What a verified webhook announced.</summary>
public enum PaymentEventType
{
    /// <summary>A charge reached <see cref="PaymentStatus.Succeeded"/>.</summary>
    ChargeSucceeded,

    /// <summary>A charge reached <see cref="PaymentStatus.Failed"/>.</summary>
    ChargeFailed,

    /// <summary>A refund succeeded.</summary>
    RefundSucceeded,

    /// <summary>A refund failed.</summary>
    RefundFailed,

    /// <summary>Authentic, but not an event this package models. The raw JSON is still carried.</summary>
    Other,
}

/// <summary>The result of checking a webhook's authenticity.</summary>
public enum WebhookOutcome
{
    /// <summary>
    /// The signature matched. <b>Authentic is not fresh:</b> Beam's signature covers the body with no
    /// timestamp or nonce, so a captured request replays for ever. Deduplicate on (charge id, status).
    /// </summary>
    Verified,

    /// <summary>No signature was present.</summary>
    SignatureMissing,

    /// <summary>A signature was present and did not match.</summary>
    SignatureMismatch,

    /// <summary>Authentic but unrecognised event name.</summary>
    UnknownEvent,

    /// <summary>The body could not be parsed.</summary>
    Malformed,
}

/// <summary>The class of an API or transport failure.</summary>
public enum FailureKind
{
    /// <summary>Credentials were rejected.</summary>
    Authentication,

    /// <summary>The request was rejected as invalid.</summary>
    Validation,

    /// <summary>The referenced object does not exist.</summary>
    NotFound,

    /// <summary>The account is not permitted to perform this operation.</summary>
    Permission,

    /// <summary>Rate limited. Retryable with backoff.</summary>
    RateLimited,

    /// <summary>A 5xx or a transport failure. Retryable with the same idempotency key.</summary>
    Transient,

    /// <summary>Anything else.</summary>
    Unknown,
}
```

- [ ] **Step 5: Run the tests and the build**

Run: `dotnet test tests/Themia.Payments.Tests` → PASS
Run: `dotnet build src/neutral/Themia.Payments --no-incremental` → expect `RS0016` for every new public member; add each line the analyzer names to `PublicAPI.Unshipped.txt` and rebuild until clean.

- [ ] **Step 6: Commit**

```bash
git add src/neutral/Themia.Payments tests/Themia.Payments.Tests Themia.sln
git commit -m "feat(payments): Money and the core enums"
```

---

### Task 2: Contracts — the gateway interface and its records

**Files:**
- Create: `src/neutral/Themia.Payments/PaymentContracts.cs`, `IPaymentGateway.cs`, `PaymentApiException.cs`
- Create: `tests/Themia.Payments.Tests/PaymentContractsTests.cs`
- Modify: `src/neutral/Themia.Payments/PublicAPI.Unshipped.txt`

**Interfaces:**
- Consumes: `Money`, the enums (Task 1).
- Produces: `PaymentFailure`, `ChargeRef`, `Charge`, `NextAction` (+ `NextAction.None` / `.Redirect` / `.ShowQr`), `ChargeCreation`, `CreateChargeRequest`, `RefundRequest`, `RefundCreation`, `IPaymentGateway`, `IPaymentGatewayCapabilities`, `PaymentApiException`.

- [ ] **Step 1: Write the failing tests**

`tests/Themia.Payments.Tests/PaymentContractsTests.cs`:

```csharp
using Themia.Payments;
using Xunit;

namespace Themia.Payments.Tests;

public class PaymentContractsTests
{
    [Fact]
    public void A_charge_reference_always_carries_the_apps_own_id()
    {
        var reference = new ChargeRef(ProviderChargeId: null, ReferenceId: "order-1");

        Assert.Equal("order-1", reference.ReferenceId);
        Assert.Null(reference.ProviderChargeId);
    }

    [Fact]
    public void Next_action_is_a_closed_hierarchy_so_a_switch_breaks_when_a_case_is_added()
    {
        NextAction action = new NextAction.ShowQr([1, 2, 3], "00020101", null);

        var described = action switch
        {
            NextAction.None => "none",
            NextAction.Redirect r => r.Url.ToString(),
            NextAction.ShowQr qr => $"qr:{qr.ImagePng.Length}",
            _ => throw new InvalidOperationException("unreachable"),
        };

        Assert.Equal("qr:3", described);
    }

    [Fact]
    public void An_api_exception_carries_the_provider_code_and_status()
    {
        var ex = new PaymentApiException(FailureKind.RateLimited, "TOO_MANY_REQUESTS_ERROR", 429);

        Assert.Equal(FailureKind.RateLimited, ex.Kind);
        Assert.Equal("TOO_MANY_REQUESTS_ERROR", ex.ProviderCode);
        Assert.Equal(429, ex.HttpStatus);
    }
}
```

- [ ] **Step 2: Run the tests and watch them fail**

Run: `dotnet test tests/Themia.Payments.Tests --filter PaymentContractsTests`
Expected: FAIL — the types do not exist.

- [ ] **Step 3: Implement the contracts**

`src/neutral/Themia.Payments/PaymentContracts.cs`:

```csharp
namespace Themia.Payments;

/// <summary>Why a payment failed.</summary>
/// <param name="Reason">The normalized reason.</param>
/// <param name="ProviderCode">The provider's own code, always carried — the normalized reason is
/// <see cref="FailureReason.Unknown"/> for codes this package has not seen, and an operator still needs the real one.</param>
/// <param name="ProviderMessage">The provider's message, when it sent one.</param>
public sealed record PaymentFailure(FailureReason Reason, string ProviderCode, string? ProviderMessage);

/// <summary>How to name a charge when reading or refunding it.</summary>
/// <param name="ProviderChargeId">The provider's id, when it issued one.</param>
/// <param name="ReferenceId">The app's own order id. Always present.</param>
/// <remarks>
/// Not a bare charge id: Beam reads a charge by its own <c>chargeId</c>, while 2C2P's Payment Inquiry takes
/// the merchant's <c>invoiceNo</c> and has no provider-side id before payment. An adapter uses whichever it
/// supports and never silently ignores the other.
/// </remarks>
public readonly record struct ChargeRef(string? ProviderChargeId, string ReferenceId);

/// <summary>A charge as the provider currently reports it.</summary>
/// <param name="ChargeId">The provider's handle. For a provider without one, the app's reference id.</param>
/// <param name="ReferenceId">The app's own order id.</param>
/// <param name="Amount">The charge amount.</param>
/// <param name="Status">Where it is.</param>
/// <param name="Failure">Why it failed, when it did.</param>
/// <param name="CompletedAt">When it reached a final status, when it has.</param>
public sealed record Charge(
    string ChargeId,
    string ReferenceId,
    Money Amount,
    PaymentStatus Status,
    PaymentFailure? Failure,
    DateTimeOffset? CompletedAt);

/// <summary>What the shopper must do next, if anything.</summary>
public abstract record NextAction
{
    private NextAction() { }

    /// <summary>Nothing more is needed.</summary>
    public sealed record None : NextAction;

    /// <summary>The shopper finishes on another page.</summary>
    /// <param name="Url">Where to send them.</param>
    public sealed record Redirect(Uri Url) : NextAction;

    /// <summary>The shopper scans a QR code.</summary>
    /// <param name="ImagePng">The decoded image bytes.</param>
    /// <param name="RawPayload">The QR's own payload, when the provider sent it.</param>
    /// <param name="Expiry">When the code stops being payable, when the provider said.</param>
    public sealed record ShowQr(byte[] ImagePng, string? RawPayload, DateTimeOffset? Expiry) : NextAction;
}

/// <summary>The outcome of creating a charge.</summary>
/// <param name="ChargeId">The provider's handle for it.</param>
/// <param name="Status">Almost always <see cref="PaymentStatus.Pending"/> at this point.</param>
/// <param name="Action">What the shopper must do next.</param>
public sealed record ChargeCreation(string ChargeId, PaymentStatus Status, NextAction Action);

/// <summary>A request to collect a payment.</summary>
public sealed record CreateChargeRequest
{
    /// <summary>The amount. Must be greater than zero.</summary>
    public required Money Amount { get; init; }

    /// <summary>
    /// The app's own order id, and the only field that ties a webhook back to an order. 2C2P caps this at
    /// 20 characters; the adapter rejects a longer one before calling.
    /// </summary>
    public required string ReferenceId { get; init; }

    /// <summary>
    /// The methods this charge may be paid with. <b>One</b> means a direct charge with that method; more
    /// than one means a hosted page offering the choice.
    /// </summary>
    public required IReadOnlyList<PaymentMethod> AllowedMethods { get; init; }

    /// <summary>Where the shopper lands after an off-site step.</summary>
    public Uri? ReturnUrl { get; init; }

    /// <summary>When the QR or link stops being payable.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>What the shopper sees the payment called.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Stable across retries of the same logical operation. Generated per call when absent — which is safe
    /// only because the generated key is reused for that call's own internal retries.
    /// </summary>
    public string? IdempotencyKey { get; init; }
}

/// <summary>A request to refund a charge.</summary>
/// <param name="Charge">Which charge.</param>
/// <param name="Amount">How much, or null for the maximum refundable amount. A partial amount is refused
/// for methods whose provider does not support one.</param>
/// <param name="Reason">Free text kept on the refund.</param>
/// <param name="IdempotencyKey">Stable across retries, as on a charge.</param>
public sealed record RefundRequest(ChargeRef Charge, Money? Amount, string? Reason, string? IdempotencyKey);

/// <summary>The outcome of requesting a refund.</summary>
/// <param name="RefundId">The provider's refund id — <b>null where the provider mints none</b>, as 2C2P's
/// Payment Process does. Key your own records on the charge, not on this.</param>
/// <param name="Status">Usually <see cref="PaymentStatus.Pending"/>; the outcome arrives by webhook.</param>
public sealed record RefundCreation(string? RefundId, PaymentStatus Status);
```

`src/neutral/Themia.Payments/IPaymentGateway.cs`:

```csharp
namespace Themia.Payments;

/// <summary>Collects a payment and reports its outcome.</summary>
/// <remarks>
/// Every charge created here is the platform's <b>own revenue</b>. Collecting money on behalf of someone
/// else and settling it later is a licensed payment business in Thailand, so that flow is out of scope by
/// law rather than by preference — see the design document's §1.
/// </remarks>
public interface IPaymentGateway
{
    /// <summary>Creates a charge.</summary>
    /// <param name="request">What to collect, and how it may be paid.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The provider's handle, the status, and what the shopper must do next.</returns>
    /// <exception cref="PaymentApiException">The provider rejected the request or could not be reached.</exception>
    Task<ChargeCreation> CreateChargeAsync(CreateChargeRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reads a charge's current state.</summary>
    /// <param name="charge">Which charge.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The charge.</returns>
    /// <exception cref="PaymentApiException">Unknown to the provider, or the provider could not be reached.</exception>
    Task<Charge> GetChargeAsync(ChargeRef charge, CancellationToken cancellationToken = default);

    /// <summary>Refunds a charge, fully or in part.</summary>
    /// <param name="request">Which charge, and how much.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The refund's id where one exists, and its status.</returns>
    /// <exception cref="PaymentApiException">Refused by the provider, or not supported for this charge.</exception>
    Task<RefundCreation> RefundAsync(RefundRequest request, CancellationToken cancellationToken = default);
}

/// <summary>What an adapter can actually do, so configuration can be checked at startup.</summary>
public interface IPaymentGatewayCapabilities
{
    /// <summary>The methods this adapter can charge with.</summary>
    IReadOnlyList<PaymentMethod> SupportedMethods { get; }
}
```

`src/neutral/Themia.Payments/PaymentApiException.cs`:

```csharp
namespace Themia.Payments;

/// <summary>A provider rejected a request, or could not be reached.</summary>
/// <remarks>
/// A <i>failed payment</i> is not a failed call: a declined charge comes back as
/// <see cref="PaymentStatus.Failed"/> with a <see cref="PaymentFailure"/>, because the request itself was
/// processed correctly. This exception is for the request going wrong.
/// </remarks>
public sealed class PaymentApiException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="kind">The class of failure.</param>
    /// <param name="providerCode">The provider's error code, or an adapter-defined code for a local rejection.</param>
    /// <param name="httpStatus">The HTTP status, or 0 when the failure was local.</param>
    /// <param name="message">A message safe to log; never include a request or response body.</param>
    /// <param name="innerException">The transport failure, when there was one.</param>
    public PaymentApiException(
        FailureKind kind,
        string providerCode,
        int httpStatus,
        string? message = null,
        Exception? innerException = null)
        : base(message ?? $"{kind}: {providerCode} (HTTP {httpStatus}).", innerException)
    {
        Kind = kind;
        ProviderCode = providerCode;
        HttpStatus = httpStatus;
    }

    /// <summary>The class of failure.</summary>
    public FailureKind Kind { get; }

    /// <summary>The provider's error code.</summary>
    public string ProviderCode { get; }

    /// <summary>The HTTP status, or 0 for a local rejection.</summary>
    public int HttpStatus { get; }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Themia.Payments.Tests` → PASS
Run: `dotnet build src/neutral/Themia.Payments --no-incremental`, add the `RS0016` lines to `PublicAPI.Unshipped.txt` until clean.

- [ ] **Step 5: Commit**

```bash
git add src/neutral/Themia.Payments tests/Themia.Payments.Tests
git commit -m "feat(payments): gateway contracts"
```

---

### Task 3: Request validation

**Files:**
- Create: `src/neutral/Themia.Payments/CreateChargeRequestValidator.cs`
- Create: `tests/Themia.Payments.Tests/CreateChargeRequestValidatorTests.cs`
- Modify: `src/neutral/Themia.Payments/PublicAPI.Unshipped.txt`

**Interfaces:**
- Consumes: `CreateChargeRequest`, `PaymentApiException` (Task 2).
- Produces: `public static class CreateChargeRequestValidator` with `public static void Validate(CreateChargeRequest request)`.

This is Review Focus item 1: a zero or negative amount must never reach a provider.

- [ ] **Step 1: Write the failing tests**

```csharp
using Themia.Payments;
using Xunit;

namespace Themia.Payments.Tests;

public class CreateChargeRequestValidatorTests
{
    private static CreateChargeRequest Valid() => new()
    {
        Amount = Money.Thb(10000),
        ReferenceId = "order-1",
        AllowedMethods = [PaymentMethod.QrPromptPay],
    };

    [Fact]
    public void A_zero_amount_is_refused_before_any_call()
    {
        var request = Valid() with { Amount = Money.Thb(0) };

        var ex = Assert.Throws<PaymentApiException>(() => CreateChargeRequestValidator.Validate(request));

        Assert.Equal(FailureKind.Validation, ex.Kind);
        Assert.Equal("amount_not_positive", ex.ProviderCode);
        Assert.Equal(0, ex.HttpStatus);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_reference_id_is_refused(string reference)
    {
        var request = Valid() with { ReferenceId = reference };

        Assert.Equal("reference_id_required",
            Assert.Throws<PaymentApiException>(() => CreateChargeRequestValidator.Validate(request)).ProviderCode);
    }

    [Fact]
    public void An_empty_method_list_is_refused()
    {
        var request = Valid() with { AllowedMethods = [] };

        Assert.Equal("no_payment_method",
            Assert.Throws<PaymentApiException>(() => CreateChargeRequestValidator.Validate(request)).ProviderCode);
    }

    [Fact]
    public void A_duplicated_method_is_refused_rather_than_silently_collapsed()
    {
        var request = Valid() with { AllowedMethods = [PaymentMethod.Card, PaymentMethod.Card] };

        Assert.Equal("duplicate_payment_method",
            Assert.Throws<PaymentApiException>(() => CreateChargeRequestValidator.Validate(request)).ProviderCode);
    }

    [Fact]
    public void An_expiry_in_the_past_is_refused()
    {
        var request = Valid() with { ExpiresAt = DateTimeOffset.UnixEpoch };

        Assert.Equal("expiry_in_the_past",
            Assert.Throws<PaymentApiException>(() => CreateChargeRequestValidator.Validate(request)).ProviderCode);
    }

    [Fact]
    public void A_valid_request_passes()
    {
        CreateChargeRequestValidator.Validate(Valid());
    }
}
```

- [ ] **Step 2: Run the tests and watch them fail**

Run: `dotnet test tests/Themia.Payments.Tests --filter CreateChargeRequestValidatorTests`
Expected: FAIL — the validator does not exist.

- [ ] **Step 3: Implement**

```csharp
namespace Themia.Payments;

/// <summary>Rejects a charge request the adapters should never send.</summary>
/// <remarks>
/// Local rejections carry <see cref="FailureKind.Validation"/> and an HTTP status of 0, so a caller can tell
/// "we refused this" from "the provider refused this" without parsing a message.
/// </remarks>
public static class CreateChargeRequestValidator
{
    /// <summary>Validates a request.</summary>
    /// <param name="request">The request.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="PaymentApiException">The request is not one any provider should be sent.</exception>
    public static void Validate(CreateChargeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Amount.MinorUnits <= 0)
        {
            throw Refuse("amount_not_positive", "A charge amount must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(request.ReferenceId))
        {
            throw Refuse("reference_id_required", "A charge needs the caller's own reference id.");
        }

        if (request.AllowedMethods.Count == 0)
        {
            throw Refuse("no_payment_method", "A charge needs at least one allowed payment method.");
        }

        if (request.AllowedMethods.Distinct().Count() != request.AllowedMethods.Count)
        {
            throw Refuse("duplicate_payment_method", "AllowedMethods contains the same method twice.");
        }

        if (request.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
        {
            throw Refuse("expiry_in_the_past", "ExpiresAt is already past.");
        }
    }

    private static PaymentApiException Refuse(string code, string message) =>
        new(FailureKind.Validation, code, httpStatus: 0, message);
}
```

- [ ] **Step 4: Run the tests, then the build**

Run: `dotnet test tests/Themia.Payments.Tests` → PASS
Run: `dotnet build src/neutral/Themia.Payments --no-incremental`; add `RS0016` lines to `PublicAPI.Unshipped.txt`.

- [ ] **Step 5: Commit**

```bash
git add src/neutral/Themia.Payments tests/Themia.Payments.Tests
git commit -m "feat(payments): reject impossible charge requests before the call"
```

---

### Task 4: Method policy by amount (§4b)

**Files:**
- Create: `src/neutral/Themia.Payments/MethodPolicy/PaymentMethodBand.cs`, `PaymentMethodPolicy.cs`, `PaymentMethodGate.cs`, `ThemiaPaymentsOptions.cs`, `PaymentMethodPolicyValidator.cs`
- Create: `tests/Themia.Payments.Tests/PaymentMethodPolicyTests.cs`, `PaymentMethodGateTests.cs`
- Modify: `src/neutral/Themia.Payments/PublicAPI.Unshipped.txt`

**Interfaces:**
- Consumes: `Money`, `PaymentMethod`, `PaymentApiException`, `IPaymentGatewayCapabilities`.
- Produces: `PaymentMethodBand(long UpToMinorUnitsInclusive, IReadOnlyList<PaymentMethod> Methods)`, `PaymentMethodPolicy` (`Currency`, `Bands`, `Above`, `IReadOnlyList<PaymentMethod> Resolve(Money)`), `ThemiaPaymentsOptions` (`PaymentMethodPolicy? MethodPolicy`), `PaymentMethodGate` (`IReadOnlyList<PaymentMethod> Apply(Money, IReadOnlyList<PaymentMethod>)`), `PaymentMethodPolicyValidator`.

This is Review Focus item 2: a policy written in baht must refuse a dollar amount.

- [ ] **Step 1: Write the failing tests**

`tests/Themia.Payments.Tests/PaymentMethodPolicyTests.cs`:

```csharp
using Themia.Payments;
using Xunit;

namespace Themia.Payments.Tests;

public class PaymentMethodPolicyTests
{
    private static PaymentMethodPolicy Policy() => new()
    {
        Currency = "THB",
        Bands = [new PaymentMethodBand(100000, [PaymentMethod.QrPromptPay, PaymentMethod.MobileBanking])],
        Above = [PaymentMethod.QrPromptPay, PaymentMethod.MobileBanking, PaymentMethod.Card],
    };

    [Fact]
    public void Below_the_boundary_only_the_cheap_methods_are_allowed()
    {
        Assert.Equal(
            [PaymentMethod.QrPromptPay, PaymentMethod.MobileBanking],
            Policy().Resolve(Money.Thb(2500)));
    }

    [Fact]
    public void The_boundary_itself_is_inclusive()
    {
        Assert.DoesNotContain(PaymentMethod.Card, Policy().Resolve(Money.Thb(100000)));
    }

    [Fact]
    public void One_satang_above_the_boundary_opens_the_expensive_methods()
    {
        Assert.Contains(PaymentMethod.Card, Policy().Resolve(Money.Thb(100001)));
    }

    [Fact]
    public void A_currency_the_policy_was_not_written_for_throws()
    {
        var ex = Assert.Throws<PaymentApiException>(() => Policy().Resolve(Money.From(2500, "USD")));

        Assert.Equal("policy_currency_mismatch", ex.ProviderCode);
    }

    [Fact]
    public void Bands_must_ascend()
    {
        var policy = new PaymentMethodPolicy
        {
            Currency = "THB",
            Bands =
            [
                new PaymentMethodBand(100000, [PaymentMethod.QrPromptPay]),
                new PaymentMethodBand(50000, [PaymentMethod.Card]),
            ],
            Above = [PaymentMethod.Card],
        };

        Assert.Contains("ascending", string.Join(" ", policy.Validate()), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_empty_band_or_empty_above_is_a_configuration_error()
    {
        var policy = new PaymentMethodPolicy
        {
            Currency = "THB",
            Bands = [new PaymentMethodBand(100000, [])],
            Above = [],
        };

        Assert.Equal(2, policy.Validate().Count);
    }
}
```

`tests/Themia.Payments.Tests/PaymentMethodGateTests.cs`:

```csharp
using Microsoft.Extensions.Options;
using Themia.Payments;
using Xunit;

namespace Themia.Payments.Tests;

public class PaymentMethodGateTests
{
    private static PaymentMethodGate Gate(PaymentMethodPolicy? policy) =>
        new(Options.Create(new ThemiaPaymentsOptions { MethodPolicy = policy }));

    private static PaymentMethodPolicy SmallTicketPolicy() => new()
    {
        Currency = "THB",
        Bands = [new PaymentMethodBand(100000, [PaymentMethod.QrPromptPay])],
        Above = [PaymentMethod.QrPromptPay, PaymentMethod.Card],
    };

    [Fact]
    public void With_no_policy_the_callers_list_passes_through_untouched()
    {
        var requested = new[] { PaymentMethod.Card, PaymentMethod.QrPromptPay };

        Assert.Equal(requested, Gate(policy: null).Apply(Money.Thb(500), requested));
    }

    [Fact]
    public void The_result_is_the_intersection_in_the_callers_order()
    {
        var allowed = Gate(SmallTicketPolicy())
            .Apply(Money.Thb(500), [PaymentMethod.Card, PaymentMethod.QrPromptPay]);

        Assert.Equal([PaymentMethod.QrPromptPay], allowed);
    }

    [Fact]
    public void An_empty_intersection_throws_and_names_both_lists()
    {
        var ex = Assert.Throws<PaymentApiException>(() =>
            Gate(SmallTicketPolicy()).Apply(Money.Thb(500), [PaymentMethod.Card]));

        Assert.Equal("method_not_allowed_for_amount", ex.ProviderCode);
        Assert.Contains("Card", ex.Message, StringComparison.Ordinal);
        Assert.Contains("QrPromptPay", ex.Message, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run the tests and watch them fail**

Run: `dotnet test tests/Themia.Payments.Tests --filter "PaymentMethodPolicyTests|PaymentMethodGateTests"`
Expected: FAIL — the types do not exist.

- [ ] **Step 3: Implement the policy, the options and the gate**

`MethodPolicy/PaymentMethodBand.cs`:

```csharp
namespace Themia.Payments;

/// <summary>One amount band and the methods allowed inside it.</summary>
/// <param name="UpToMinorUnitsInclusive">The band's upper bound, <b>in minor units and inclusive</b>:
/// <c>100000</c> is 1,000.00 THB, and an amount of exactly that is inside this band.</param>
/// <param name="Methods">The methods allowed at or below that amount.</param>
public sealed record PaymentMethodBand(long UpToMinorUnitsInclusive, IReadOnlyList<PaymentMethod> Methods);
```

`MethodPolicy/PaymentMethodPolicy.cs`:

```csharp
namespace Themia.Payments;

/// <summary>Which methods an amount may be paid with.</summary>
/// <remarks>
/// The fee on a payment depends on how it is paid: a QR transfer is a flat fee, a card is a percentage. On a
/// small charge a card fee can be a tenth of the whole amount. This is the rule an adopter writes to say so —
/// it does not pick the cheapest method, reorder anything, or model fees, because rates are per-merchant
/// contract terms that change without a release.
/// </remarks>
public sealed class PaymentMethodPolicy
{
    /// <summary>The ISO 4217 code these bands are written in.</summary>
    public string Currency { get; init; } = "THB";

    /// <summary>Bands in ascending order of their upper bound.</summary>
    public IReadOnlyList<PaymentMethodBand> Bands { get; init; } = [];

    /// <summary>The methods allowed above the last band.</summary>
    public IReadOnlyList<PaymentMethod> Above { get; init; } = [];

    /// <summary>The methods this policy allows for an amount.</summary>
    /// <param name="amount">The charge amount.</param>
    /// <returns>The allowed methods.</returns>
    /// <exception cref="PaymentApiException">The amount is in another currency.</exception>
    public IReadOnlyList<PaymentMethod> Resolve(Money amount)
    {
        if (!string.Equals(amount.Currency, Currency, StringComparison.Ordinal))
        {
            throw new PaymentApiException(
                FailureKind.Validation, "policy_currency_mismatch", httpStatus: 0,
                $"The method policy is written in {Currency}; this charge is in {amount.Currency}.");
        }

        foreach (var band in Bands)
        {
            if (amount.MinorUnits <= band.UpToMinorUnitsInclusive)
            {
                return band.Methods;
            }
        }

        return Above;
    }

    /// <summary>Configuration errors in this policy, empty when it is usable.</summary>
    /// <returns>One message per problem.</returns>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (Currency.Length != 3 || !Currency.All(char.IsAsciiLetter))
        {
            errors.Add($"MethodPolicy.Currency '{Currency}' is not an ISO 4217 code.");
        }

        for (var i = 0; i < Bands.Count; i++)
        {
            if (Bands[i].Methods.Count == 0)
            {
                errors.Add($"MethodPolicy.Bands[{i}] allows no method, so a charge in that band could never be paid.");
            }

            if (i > 0 && Bands[i].UpToMinorUnitsInclusive <= Bands[i - 1].UpToMinorUnitsInclusive)
            {
                errors.Add("MethodPolicy.Bands must be in ascending order of UpToMinorUnitsInclusive, with no duplicates.");
            }
        }

        if (Above.Count == 0)
        {
            errors.Add("MethodPolicy.Above allows no method, so any amount above the last band could never be paid.");
        }

        return errors;
    }

    /// <summary>Every method this policy can ever produce.</summary>
    /// <returns>The distinct methods named anywhere in it.</returns>
    public IReadOnlyList<PaymentMethod> AllNamedMethods() =>
        Bands.SelectMany(b => b.Methods).Concat(Above).Distinct().ToArray();
}
```

`MethodPolicy/ThemiaPaymentsOptions.cs`:

```csharp
namespace Themia.Payments;

/// <summary>Options shared by every adapter.</summary>
public sealed class ThemiaPaymentsOptions
{
    /// <summary>The configuration section these bind from.</summary>
    public const string SectionName = "Payments";

    /// <summary>
    /// Restricts which methods an amount may be paid with. <b>Null means no restriction</b> — the caller's
    /// own list passes through untouched.
    /// </summary>
    public PaymentMethodPolicy? MethodPolicy { get; set; }
}
```

`MethodPolicy/PaymentMethodGate.cs`:

```csharp
using Microsoft.Extensions.Options;

namespace Themia.Payments;

/// <summary>Applies the configured <see cref="PaymentMethodPolicy"/> to a caller's chosen methods.</summary>
/// <remarks>
/// Adapters call this once, before building a payload. It intersects rather than replaces: a caller that
/// asked for one method usually has a reason (a saved card, a retry of a failed attempt), so an empty
/// intersection is an error naming both lists rather than a silent substitution.
/// </remarks>
public sealed class PaymentMethodGate
{
    private readonly IOptions<ThemiaPaymentsOptions> options;

    /// <summary>Creates the gate.</summary>
    /// <param name="options">The shared options.</param>
    public PaymentMethodGate(IOptions<ThemiaPaymentsOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    /// <summary>The methods a charge may actually offer.</summary>
    /// <param name="amount">The charge amount.</param>
    /// <param name="requested">What the caller asked for.</param>
    /// <returns>The caller's list, filtered by the policy and in the caller's order.</returns>
    /// <exception cref="PaymentApiException">The policy leaves nothing the caller asked for.</exception>
    public IReadOnlyList<PaymentMethod> Apply(Money amount, IReadOnlyList<PaymentMethod> requested)
    {
        ArgumentNullException.ThrowIfNull(requested);

        var policy = options.Value.MethodPolicy;
        if (policy is null)
        {
            return requested;
        }

        var allowed = policy.Resolve(amount);
        var kept = requested.Where(allowed.Contains).ToArray();
        if (kept.Length > 0)
        {
            return kept;
        }

        throw new PaymentApiException(
            FailureKind.Validation, "method_not_allowed_for_amount", httpStatus: 0,
            $"The caller asked for [{string.Join(", ", requested)}] but the policy allows " +
            $"[{string.Join(", ", allowed)}] at {amount.MinorUnits} {amount.Currency}.");
    }
}
```

`MethodPolicy/PaymentMethodPolicyValidator.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Themia.Payments;

/// <summary>Fails startup on a policy the registered adapter could never satisfy.</summary>
/// <remarks>
/// An <see cref="IValidateOptions{TOptions}"/> rather than a fluent predicate, because the check needs the
/// adapter's <see cref="IPaymentGatewayCapabilities"/>. A policy that can produce a method the adapter does
/// not support is a configuration error, and boot is the only safe time to find it.
/// <para>
/// Takes <see cref="IServiceProvider"/> and resolves the capabilities at validation time, rather than taking
/// them as a constructor parameter, for two reasons. The capabilities are optional — the core can be
/// registered with no adapter — and an optional constructor dependency cannot be expressed with the
/// type-based <c>ServiceDescriptor</c> that <c>TryAddEnumerable</c> requires. And resolving late means the
/// order of <c>AddThemiaPayments</c> and the adapter's own <c>Add…</c> does not matter.
/// </para>
/// </remarks>
internal sealed class PaymentMethodPolicyValidator : IValidateOptions<ThemiaPaymentsOptions>
{
    private readonly IServiceProvider services;

    public PaymentMethodPolicyValidator(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        this.services = services;
    }

    public ValidateOptionsResult Validate(string? name, ThemiaPaymentsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MethodPolicy is not { } policy)
        {
            return ValidateOptionsResult.Success;
        }

        var errors = policy.Validate().ToList();

        if (services.GetService<IPaymentGatewayCapabilities>() is { } capabilities)
        {
            var unsupported = policy.AllNamedMethods().Except(capabilities.SupportedMethods).ToArray();
            if (unsupported.Length > 0)
            {
                errors.Add(
                    $"MethodPolicy names [{string.Join(", ", unsupported)}], which the registered payment " +
                    $"adapter does not support (it supports [{string.Join(", ", capabilities.SupportedMethods)}]).");
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Themia.Payments.Tests` → PASS
Run: `dotnet build src/neutral/Themia.Payments --no-incremental`; add `RS0016` lines.

- [ ] **Step 5: Commit**

```bash
git add src/neutral/Themia.Payments tests/Themia.Payments.Tests
git commit -m "feat(payments): optional method-by-amount policy"
```

---

### Task 5: Webhook contracts and core DI

**Files:**
- Create: `src/neutral/Themia.Payments/Webhooks/IPaymentWebhookVerifier.cs`, `WebhookVerification.cs`, `PaymentEvent.cs`
- Create: `src/neutral/Themia.Payments/DependencyInjection/PaymentsServiceCollectionExtensions.cs`
- Create: `tests/Themia.Payments.Tests/DependencyInjectionTests.cs`
- Modify: `src/neutral/Themia.Payments/PublicAPI.Unshipped.txt`

**Interfaces:**
- Produces: `IPaymentWebhookVerifier.Verify(ReadOnlySpan<byte>, IReadOnlyDictionary<string,string>) -> WebhookVerification`, `WebhookVerification(WebhookOutcome Outcome, PaymentEvent? Event)`, `PaymentEvent`, `AddThemiaPayments(IServiceCollection, Action<ThemiaPaymentsOptions>?)`.

- [ ] **Step 1: Write the failing test**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Themia.Payments;
using Themia.Payments.DependencyInjection;
using Xunit;

namespace Themia.Payments.Tests;

public class DependencyInjectionTests
{
    [Fact]
    public void AddThemiaPayments_registers_the_gate_and_the_options()
    {
        var services = new ServiceCollection();

        services.AddThemiaPayments(o => o.MethodPolicy = new PaymentMethodPolicy
        {
            Currency = "THB",
            Bands = [new PaymentMethodBand(100000, [PaymentMethod.QrPromptPay])],
            Above = [PaymentMethod.QrPromptPay, PaymentMethod.Card],
        });

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<PaymentMethodGate>());
        Assert.Equal("THB", provider.GetRequiredService<IOptions<ThemiaPaymentsOptions>>().Value.MethodPolicy!.Currency);
    }

    [Fact]
    public void Registering_twice_does_not_throw_and_adds_the_validator_once()
    {
        // The adapter's Add… calls AddThemiaPayments itself, and the host usually calls it again with its
        // own policy. Both calls must be safe, and must not stack two validators.
        var services = new ServiceCollection();

        services.AddThemiaPayments();
        services.AddThemiaPayments(o => o.MethodPolicy = null);

        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<ThemiaPaymentsOptions>));
    }

    [Fact]
    public void Capabilities_registered_after_the_core_are_still_checked()
    {
        var services = new ServiceCollection();
        services.AddThemiaPayments(o => o.MethodPolicy = new PaymentMethodPolicy
        {
            Currency = "THB",
            Bands = [new PaymentMethodBand(100000, [PaymentMethod.QrPromptPay])],
            Above = [PaymentMethod.Wallet],
        });
        services.AddSingleton<IPaymentGatewayCapabilities>(new QrOnlyCapabilities());   // after, on purpose

        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<ThemiaPaymentsOptions>>().Value);
    }

    [Fact]
    public void A_policy_naming_a_method_the_adapter_cannot_serve_fails_validation()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPaymentGatewayCapabilities>(new QrOnlyCapabilities());
        services.AddThemiaPayments(o => o.MethodPolicy = new PaymentMethodPolicy
        {
            Currency = "THB",
            Bands = [new PaymentMethodBand(100000, [PaymentMethod.QrPromptPay])],
            Above = [PaymentMethod.Wallet],
        });

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<ThemiaPaymentsOptions>>().Value);
        Assert.Contains("Wallet", string.Join(" ", ex.Failures), StringComparison.Ordinal);
    }

    private sealed class QrOnlyCapabilities : IPaymentGatewayCapabilities
    {
        public IReadOnlyList<PaymentMethod> SupportedMethods => [PaymentMethod.QrPromptPay];
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test tests/Themia.Payments.Tests --filter DependencyInjectionTests`
Expected: FAIL — `AddThemiaPayments` does not exist.

- [ ] **Step 3: Implement the webhook contracts**

`Webhooks/PaymentEvent.cs`:

```csharp
namespace Themia.Payments;

/// <summary>What a verified webhook said.</summary>
/// <param name="Type">Which event.</param>
/// <param name="ChargeId">The provider's charge id, when the event carries one.</param>
/// <param name="ReferenceId">The app's own reference, when the event carries one.</param>
/// <param name="Amount">The amount, when the event carries one.</param>
/// <param name="Status">The status the event announces.</param>
/// <param name="OccurredAt">When the provider says it happened.</param>
/// <param name="RawJson">The body as received, for anything provider-specific.</param>
public sealed record PaymentEvent(
    PaymentEventType Type,
    string? ChargeId,
    string? ReferenceId,
    Money? Amount,
    PaymentStatus Status,
    DateTimeOffset OccurredAt,
    string RawJson);
```

`Webhooks/WebhookVerification.cs`:

```csharp
namespace Themia.Payments;

/// <summary>The outcome of verifying a webhook, and the event when it was authentic.</summary>
/// <param name="Outcome">What the check concluded.</param>
/// <param name="Event">The event, when <paramref name="Outcome"/> is <see cref="WebhookOutcome.Verified"/>.</param>
public sealed record WebhookVerification(WebhookOutcome Outcome, PaymentEvent? Event);
```

`Webhooks/IPaymentWebhookVerifier.cs`:

```csharp
namespace Themia.Payments;

/// <summary>Checks that a webhook came from the provider, and reads it.</summary>
/// <remarks>
/// Takes <b>bytes, not a parsed object</b>: providers sign the exact body they sent, so anything that
/// deserializes and re-serializes before verifying will mismatch. An ASP.NET Core host must call
/// <c>EnableBuffering()</c> and read the body before model binding.
/// <para>
/// Verification proves origin, not freshness. Beam's signature covers the body with no timestamp and no
/// nonce, so a captured request replays for ever; deduplication is the consumer's, on (charge id, status).
/// </para>
/// </remarks>
public interface IPaymentWebhookVerifier
{
    /// <summary>Verifies and reads a webhook.</summary>
    /// <param name="rawBody">The body exactly as received.</param>
    /// <param name="headers">The request headers, matched case-insensitively by the implementation.</param>
    /// <returns>The outcome, and the event when authentic.</returns>
    WebhookVerification Verify(ReadOnlySpan<byte> rawBody, IReadOnlyDictionary<string, string> headers);
}
```

`DependencyInjection/PaymentsServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Themia.Payments.DependencyInjection;

/// <summary>DI entry point for the provider-agnostic payment pieces.</summary>
public static class PaymentsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ThemiaPaymentsOptions"/> (validated on start) and <see cref="PaymentMethodGate"/>.
    /// Call it <b>after</b> the adapter's own <c>Add…</c>, so the policy can be checked against what that
    /// adapter supports.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Sets the shared options; omit for defaults (no method policy).</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    public static IServiceCollection AddThemiaPayments(
        this IServiceCollection services, Action<ThemiaPaymentsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.AddOptions<ThemiaPaymentsOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        options.ValidateOnStart();

        // Type-based descriptor, never a factory lambda: TryAddEnumerable dedupes on the implementation
        // type, and a factory descriptor has none, so it throws ArgumentException ("indistinguishable from
        // other services registered") at registration — every host would fail to start. Verified on net10.0;
        // Themia.Totp registers its validator the same way for the same reason.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<ThemiaPaymentsOptions>, PaymentMethodPolicyValidator>());
        services.TryAddSingleton<PaymentMethodGate>();
        return services;
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Themia.Payments.Tests` → PASS. If the validation test passes only on `BuildServiceProvider`, keep the assertion on `IOptions<>.Value`, which is what forces validation outside a host.
Run: `dotnet build src/neutral/Themia.Payments --no-incremental`; add `RS0016` lines.

- [ ] **Step 5: Commit**

```bash
git add src/neutral/Themia.Payments tests/Themia.Payments.Tests
git commit -m "feat(payments): webhook contracts and core DI"
```

---

### Task 6: Beam project, options, DI and HTTP plumbing

**Files:**
- Create: `src/neutral/Themia.Payments.Beam/Themia.Payments.Beam.csproj`, `BeamEnvironment.cs`, `BeamOptions.cs`, `Internal/BeamHttp.cs`, `DependencyInjection/BeamServiceCollectionExtensions.cs`, `PublicAPI.*.txt`
- Create: `tests/Themia.Payments.Beam.Tests/Themia.Payments.Beam.Tests.csproj`, `BeamOptionsTests.cs`, `StubHandler.cs`
- Modify: `Themia.sln`

**Interfaces:**
- Consumes: the core (Tasks 1–5).
- Produces: `BeamEnvironment { Playground, Production }`, `BeamOptions { MerchantId, ApiKey, Environment, WebhookHmacKey, PartnerId, Timeout }`, `AddThemiaPaymentsBeam(IServiceCollection, Action<BeamOptions>)`, and the internal `BeamHttp.CreateRequest(...)` helper used by Tasks 7–9.
- Produces for tests: `StubHandler` — an `HttpMessageHandler` that records requests and replays scripted responses.

- [ ] **Step 1: Create the projects**

Copy `src/neutral/Themia.Geo.Google/Themia.Geo.Google.csproj` to the new package and change `PackageId`, `Description`, `InternalsVisibleTo` and the `ProjectReference` (to `Themia.Payments`). Keep `Microsoft.Extensions.Http`. Test csproj copies `tests/Themia.Geo.Google.Tests`.

```bash
dotnet sln Themia.sln add src/neutral/Themia.Payments.Beam --solution-folder neutral
dotnet sln Themia.sln add tests/Themia.Payments.Beam.Tests --solution-folder tests
```

- [ ] **Step 2: Write the failing tests**

`tests/Themia.Payments.Beam.Tests/BeamOptionsTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Themia.Payments;
using Themia.Payments.Beam;
using Themia.Payments.Beam.DependencyInjection;
using Xunit;

namespace Themia.Payments.Beam.Tests;

public class BeamOptionsTests
{
    [Fact]
    public void Playground_and_production_have_different_base_addresses()
    {
        Assert.Equal(new Uri("https://playground.api.beamcheckout.com"), BeamOptions.BaseAddressFor(BeamEnvironment.Playground));
        Assert.Equal(new Uri("https://api.beamcheckout.com"), BeamOptions.BaseAddressFor(BeamEnvironment.Production));
    }

    [Fact]
    public void A_missing_merchant_id_fails_validation_at_startup()
    {
        var services = new ServiceCollection();
        services.AddThemiaPaymentsBeam(o =>
        {
            o.MerchantId = "";
            o.ApiKey = "key";
            o.Environment = BeamEnvironment.Playground;
        });

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<BeamOptions>>().Value);
        Assert.Contains("MerchantId", string.Join(" ", ex.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void The_adapter_declares_what_it_can_charge_with()
    {
        var services = new ServiceCollection();
        services.AddThemiaPaymentsBeam(o =>
        {
            o.MerchantId = "m";
            o.ApiKey = "k";
            o.Environment = BeamEnvironment.Playground;
        });

        using var provider = services.BuildServiceProvider();

        Assert.Equal(
            [PaymentMethod.QrPromptPay, PaymentMethod.Card],
            provider.GetRequiredService<IPaymentGatewayCapabilities>().SupportedMethods);
    }
}
```

`tests/Themia.Payments.Beam.Tests/StubHandler.cs`:

```csharp
using System.Net;

namespace Themia.Payments.Beam.Tests;

/// <summary>Replays scripted responses and records what was sent, including each attempt's headers.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> responses = new();

    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string> Bodies { get; } = [];

    public StubHandler Enqueue(HttpStatusCode status, string body)
    {
        responses.Enqueue((status, body));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));

        var (status, body) = responses.Count > 0 ? responses.Dequeue() : (HttpStatusCode.OK, "{}");
        return new HttpResponseMessage(status) { Content = new StringContent(body) };
    }
}
```

- [ ] **Step 3: Run the tests and watch them fail**

Run: `dotnet test tests/Themia.Payments.Beam.Tests`
Expected: FAIL — the Beam types do not exist.

- [ ] **Step 4: Implement options, DI and the HTTP helper**

`BeamEnvironment.cs`:

```csharp
namespace Themia.Payments.Beam;

/// <summary>Which Beam environment to call.</summary>
public enum BeamEnvironment
{
    /// <summary>The sandbox. Same API, no real money.</summary>
    Playground,

    /// <summary>Live.</summary>
    Production,
}
```

`BeamOptions.cs`:

```csharp
namespace Themia.Payments.Beam;

/// <summary>Credentials and environment for the Beam adapter.</summary>
public sealed class BeamOptions
{
    /// <summary>The configuration section these bind from.</summary>
    public const string SectionName = "Payments:Beam";

    /// <summary>The merchant id, used as the Basic-auth user.</summary>
    public string MerchantId { get; set; } = "";

    /// <summary>The merchant API key, used as the Basic-auth password. Never logged.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Which environment to call.</summary>
    public BeamEnvironment Environment { get; set; } = BeamEnvironment.Playground;

    /// <summary>The base64 HMAC key from Lighthouse, used to verify webhooks. Never logged.</summary>
    public string? WebhookHmacKey { get; set; }

    /// <summary>The partner id, for a partner acting for a merchant. Sent as <c>X-Beam-Partner-ID</c>.</summary>
    public string? PartnerId { get; set; }

    /// <summary>Per-request timeout. Default 30 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The base address for an environment.</summary>
    /// <param name="environment">The environment.</param>
    /// <returns>Its base address.</returns>
    public static Uri BaseAddressFor(BeamEnvironment environment) => environment switch
    {
        BeamEnvironment.Playground => new Uri("https://playground.api.beamcheckout.com"),
        BeamEnvironment.Production => new Uri("https://api.beamcheckout.com"),
        _ => throw new ArgumentOutOfRangeException(nameof(environment)),
    };
}
```

`DependencyInjection/BeamServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Payments.DependencyInjection;

namespace Themia.Payments.Beam.DependencyInjection;

/// <summary>DI entry point for the Beam adapter.</summary>
public static class BeamServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="BeamOptions"/> (validated with <c>ValidateOnStart</c>), the named
    /// <see cref="HttpClient"/>, and <see cref="BeamPaymentGateway"/> as <see cref="IPaymentGateway"/>,
    /// <see cref="IPaymentGatewayCapabilities"/> and (through <see cref="BeamWebhookVerifier"/>)
    /// <see cref="IPaymentWebhookVerifier"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Sets the merchant id, API key and environment.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configure"/> is null.</exception>
    public static IServiceCollection AddThemiaPaymentsBeam(
        this IServiceCollection services, Action<BeamOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<BeamOptions>()
            .Configure(configure)
            .Validate(o => !string.IsNullOrWhiteSpace(o.MerchantId), "BeamOptions.MerchantId must be set.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.ApiKey), "BeamOptions.ApiKey must be set.")
            .Validate(o => o.Timeout > TimeSpan.Zero, "BeamOptions.Timeout must be positive.")
            .ValidateOnStart();

        services.AddHttpClient(BeamPaymentGateway.HttpClientName, (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<BeamOptions>>().Value;
            client.BaseAddress = BeamOptions.BaseAddressFor(options.Environment);
            client.Timeout = options.Timeout;
        });

        services.TryAddSingleton<BeamPaymentGateway>();
        services.TryAddSingleton<IPaymentGateway>(sp => sp.GetRequiredService<BeamPaymentGateway>());
        services.TryAddSingleton<IPaymentGatewayCapabilities>(sp => sp.GetRequiredService<BeamPaymentGateway>());
        services.TryAddSingleton<IPaymentWebhookVerifier, BeamWebhookVerifier>();

        // So the gate exists even when the host forgot the core call. AddThemiaPayments is idempotent:
        // it uses TryAdd and TryAddEnumerable throughout.
        services.AddThemiaPayments();
        return services;
    }
}
```

The same shape, with `TwoCTwoPOptions` and `TwoCTwoPPaymentGateway`, is what Task 13 writes for 2C2P.

`Internal/BeamHttp.cs` holds the header work in one place:

```csharp
using System.Net.Http.Headers;
using System.Text;

namespace Themia.Payments.Beam.Internal;

/// <summary>Builds Beam requests: Basic auth, the optional partner header, and the idempotency key.</summary>
internal static class BeamHttp
{
    public const string IdempotencyHeader = "x-beam-idempotency-key";
    public const string PartnerHeader = "X-Beam-Partner-ID";

    public static HttpRequestMessage Create(
        HttpMethod method, string path, BeamOptions options, HttpContent? content = null, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.MerchantId}:{options.ApiKey}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        if (!string.IsNullOrWhiteSpace(options.PartnerId))
        {
            request.Headers.TryAddWithoutValidation(PartnerHeader, options.PartnerId);
        }

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            request.Headers.TryAddWithoutValidation(IdempotencyHeader, idempotencyKey);
        }

        return request;
    }
}
```

- [ ] **Step 5: Run the tests, build, commit**

Run: `dotnet test tests/Themia.Payments.Beam.Tests` → PASS
Run: `dotnet build src/neutral/Themia.Payments.Beam --no-incremental`; fill `PublicAPI.Unshipped.txt`.

```bash
git add src/neutral/Themia.Payments.Beam tests/Themia.Payments.Beam.Tests Themia.sln
git commit -m "feat(payments-beam): options, DI and request plumbing"
```

---

### Task 7: Beam — create a charge

**Files:**
- Create: `src/neutral/Themia.Payments.Beam/BeamPaymentGateway.cs`, `BeamMapping.cs`
- Create: `tests/Themia.Payments.Beam.Tests/BeamCreateChargeTests.cs`
- Modify: `PublicAPI.Unshipped.txt`

**Interfaces:**
- Consumes: `BeamOptions`, `BeamHttp`, `PaymentMethodGate`, `CreateChargeRequestValidator`.
- Produces: `BeamPaymentGateway` (implements `IPaymentGateway`, `IPaymentGatewayCapabilities`; `public const string HttpClientName = "themia-payments-beam"`), `BeamMapping.ToBeamMethod(PaymentMethod)`, `BeamMapping.ToNextAction(JsonElement)`.

Beam v1 supports `QrPromptPay` and `Card` only (`MobileBanking` and `Wallet` are reachable only through a payment link, which is Task 11's Beam-only surface).

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Themia.Payments;
using Themia.Payments.Beam;
using Xunit;

namespace Themia.Payments.Beam.Tests;

public class BeamCreateChargeTests
{
    private static readonly string EncodedImageResponse = """
    {
      "actionRequired": "ENCODED_IMAGE",
      "chargeId": "ch_2xTsz7Qit55pahSvKfJG3UMkpFQ",
      "encodedImage": {
        "expiry": "2025-08-24T14:15:22Z",
        "imageBase64Encoded": "aGVsbG8=",
        "rawData": "00020101"
      },
      "paymentMethodType": "QR_PROMPT_PAY"
    }
    """;

    private static (BeamPaymentGateway Gateway, StubHandler Handler) Build(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new StubHandler().Enqueue(status, body);
        var client = new HttpClient(handler) { BaseAddress = BeamOptions.BaseAddressFor(BeamEnvironment.Playground) };
        var options = Options.Create(new BeamOptions { MerchantId = "m", ApiKey = "k" });
        var gate = new PaymentMethodGate(Options.Create(new ThemiaPaymentsOptions()));
        return (new BeamPaymentGateway(new StubClientFactory(client), options, gate), handler);
    }

    [Fact]
    public async Task A_qr_charge_sends_minor_units_and_the_reference_id()
    {
        var (gateway, handler) = Build(EncodedImageResponse);

        await gateway.CreateChargeAsync(new CreateChargeRequest
        {
            Amount = Money.Thb(10000),
            ReferenceId = "order_190822",
            AllowedMethods = [PaymentMethod.QrPromptPay],
        });

        using var sent = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal(10000, sent.RootElement.GetProperty("amount").GetInt64());
        Assert.Equal("THB", sent.RootElement.GetProperty("currency").GetString());
        Assert.Equal("order_190822", sent.RootElement.GetProperty("referenceId").GetString());
        Assert.Equal("QR_PROMPT_PAY",
            sent.RootElement.GetProperty("paymentMethod").GetProperty("paymentMethodType").GetString());
        Assert.Equal("/api/v1/charges", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task An_encoded_image_response_becomes_a_show_qr_action()
    {
        var (gateway, _) = Build(EncodedImageResponse);

        var creation = await gateway.CreateChargeAsync(new CreateChargeRequest
        {
            Amount = Money.Thb(10000),
            ReferenceId = "order_190822",
            AllowedMethods = [PaymentMethod.QrPromptPay],
        });

        var qr = Assert.IsType<NextAction.ShowQr>(creation.Action);
        Assert.Equal("hello"u8.ToArray(), qr.ImagePng);
        Assert.Equal("00020101", qr.RawPayload);
        Assert.Equal(PaymentStatus.Pending, creation.Status);
        Assert.Equal("ch_2xTsz7Qit55pahSvKfJG3UMkpFQ", creation.ChargeId);
    }

    [Fact]
    public async Task A_redirect_response_becomes_a_redirect_action()
    {
        var (gateway, _) = Build("""
        { "actionRequired": "REDIRECT", "chargeId": "ch_1", "redirect": { "redirectUrl": "https://pay.example/1" } }
        """);

        var creation = await gateway.CreateChargeAsync(new CreateChargeRequest
        {
            Amount = Money.Thb(500),
            ReferenceId = "order-2",
            AllowedMethods = [PaymentMethod.Card],
        });

        Assert.Equal(new Uri("https://pay.example/1"), Assert.IsType<NextAction.Redirect>(creation.Action).Url);
    }

    [Fact]
    public async Task A_method_this_adapter_cannot_charge_with_is_refused_before_the_call()
    {
        var (gateway, handler) = Build("{}");

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => gateway.CreateChargeAsync(new CreateChargeRequest
        {
            Amount = Money.Thb(500),
            ReferenceId = "order-3",
            AllowedMethods = [PaymentMethod.Wallet],
        }));

        Assert.Equal("method_not_supported", ex.ProviderCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_beam_error_becomes_a_typed_exception()
    {
        var (gateway, _) = Build("""
        { "code": 401, "message": "invalid authentication credentials",
          "error": { "errorCode": "INVALID_CREDENTIALS_ERROR", "errorMessage": "invalid authentication credentials" } }
        """, HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => gateway.CreateChargeAsync(new CreateChargeRequest
        {
            Amount = Money.Thb(500),
            ReferenceId = "order-4",
            AllowedMethods = [PaymentMethod.QrPromptPay],
        }));

        Assert.Equal(FailureKind.Authentication, ex.Kind);
        Assert.Equal("INVALID_CREDENTIALS_ERROR", ex.ProviderCode);
        Assert.Equal(401, ex.HttpStatus);
    }

    private sealed class StubClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
```

- [ ] **Step 2: Run the tests and watch them fail**

Run: `dotnet test tests/Themia.Payments.Beam.Tests --filter BeamCreateChargeTests`
Expected: FAIL — `BeamPaymentGateway` does not exist.

- [ ] **Step 3: Implement the gateway's create path and the mapping**

`BeamMapping.cs` holds every translation in one file: method → `paymentMethodType`, `actionRequired` → `NextAction`, `status` → `PaymentStatus`, `failureCode` → `FailureReason`, `errorCode` → `FailureKind`. Write it as `internal static class BeamMapping` with one method per direction; Task 8 adds the status and failure maps, this task adds:

```csharp
public static string ToBeamMethod(PaymentMethod method) => method switch
{
    PaymentMethod.QrPromptPay => "QR_PROMPT_PAY",
    PaymentMethod.Card => "CARD",
    _ => throw new PaymentApiException(
        FailureKind.Validation, "method_not_supported", httpStatus: 0,
        $"The Beam adapter charges with QrPromptPay or Card; {method} is reachable only through a payment link."),
};

public static FailureKind ToFailureKind(int httpStatus, string errorCode) => errorCode switch
{
    "INVALID_CREDENTIALS_ERROR" => FailureKind.Authentication,
    "API_VALIDATION_ERROR" or "INVALID_JSON_ERROR" or "INVALID_XML_ERROR" => FailureKind.Validation,
    "NOT_FOUND_ERROR" => FailureKind.NotFound,
    "NO_PERMISSION_ERROR" or "OPERATION_NOT_ALLOWED_ERROR" => FailureKind.Permission,
    "TOO_MANY_REQUESTS_ERROR" => FailureKind.RateLimited,
    _ => httpStatus >= 500 ? FailureKind.Transient : FailureKind.Unknown,
};
```

`BeamPaymentGateway.CreateChargeAsync` in order: `CreateChargeRequestValidator.Validate(request)`, `gate.Apply(request.Amount, request.AllowedMethods)`, map the single method (more than one is Task 11's payment-link path — for now throw `PaymentApiException(Validation, "multiple_methods_need_a_payment_link", 0)`), build the JSON with `System.Text.Json`, send through `BeamHttp.Create`, translate a non-2xx into `PaymentApiException`, and read `actionRequired`.

- [ ] **Step 4: Run the tests, build, commit**

Run: `dotnet test tests/Themia.Payments.Beam.Tests` → PASS

```bash
git add src/neutral/Themia.Payments.Beam tests/Themia.Payments.Beam.Tests
git commit -m "feat(payments-beam): create a charge"
```

---

### Task 8: Beam — read a charge, and the retry/idempotency policy

**Files:**
- Modify: `src/neutral/Themia.Payments.Beam/BeamPaymentGateway.cs`, `BeamMapping.cs`, `Internal/BeamHttp.cs`
- Create: `tests/Themia.Payments.Beam.Tests/BeamGetChargeTests.cs`, `BeamRetryTests.cs`

**Interfaces:**
- Produces: `GetChargeAsync` over `GET /api/v1/charges/{chargeId}`, `BeamMapping.ToStatus(string)`, `BeamMapping.ToFailure(string?, string?)`, and `BeamHttp.SendWithRetryAsync(...)`.

This is Review Focus item 4: a retry must carry the same idempotency key.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task A_failed_charge_carries_the_reason_and_the_raw_code()
{
    var (gateway, _) = Build("""
    { "chargeId": "ch_1", "referenceId": "order-1", "status": "FAILED", "currency": "THB", "amount": 199,
      "failureCode": "CH_INSUFFICIENT_FUNDS", "transactionTime": "2026-09-22T10:00:00Z" }
    """);

    var charge = await gateway.GetChargeAsync(new ChargeRef("ch_1", "order-1"));

    Assert.Equal(PaymentStatus.Failed, charge.Status);
    Assert.Equal(FailureReason.InsufficientFunds, charge.Failure!.Reason);
    Assert.Equal("CH_INSUFFICIENT_FUNDS", charge.Failure.ProviderCode);
    Assert.Equal(Money.Thb(199), charge.Amount);
}

[Fact]
public async Task Reading_a_charge_without_a_provider_id_is_refused_because_beam_has_no_lookup_by_reference()
{
    var (gateway, handler) = Build("{}");

    var ex = await Assert.ThrowsAsync<PaymentApiException>(
        () => gateway.GetChargeAsync(new ChargeRef(ProviderChargeId: null, ReferenceId: "order-1")));

    Assert.Equal("provider_charge_id_required", ex.ProviderCode);
    Assert.Empty(handler.Requests);
}

[Fact]
public async Task A_retry_after_a_500_reuses_the_same_idempotency_key()
{
    var handler = new StubHandler()
        .Enqueue(HttpStatusCode.InternalServerError, "{}")
        .Enqueue(HttpStatusCode.OK, EncodedImageResponse);
    var gateway = BuildWith(handler);

    await gateway.CreateChargeAsync(new CreateChargeRequest
    {
        Amount = Money.Thb(10000),
        ReferenceId = "order-1",
        AllowedMethods = [PaymentMethod.QrPromptPay],
        IdempotencyKey = "key-1",
    });

    Assert.Equal(2, handler.Requests.Count);
    Assert.All(handler.Requests, r =>
        Assert.Equal("key-1", r.Headers.GetValues("x-beam-idempotency-key").Single()));
}

[Fact]
public async Task A_generated_key_is_also_stable_across_attempts()
{
    var handler = new StubHandler()
        .Enqueue(HttpStatusCode.ServiceUnavailable, "{}")
        .Enqueue(HttpStatusCode.OK, EncodedImageResponse);
    var gateway = BuildWith(handler);

    await gateway.CreateChargeAsync(new CreateChargeRequest
    {
        Amount = Money.Thb(10000),
        ReferenceId = "order-1",
        AllowedMethods = [PaymentMethod.QrPromptPay],
    });

    var keys = handler.Requests.Select(r => r.Headers.GetValues("x-beam-idempotency-key").Single()).Distinct();
    Assert.Single(keys);
}

[Fact]
public async Task A_400_is_not_retried()
{
    var handler = new StubHandler()
        .Enqueue(HttpStatusCode.BadRequest, """{ "error": { "errorCode": "API_VALIDATION_ERROR" } }""");
    var gateway = BuildWith(handler);

    await Assert.ThrowsAsync<PaymentApiException>(() => gateway.CreateChargeAsync(new CreateChargeRequest
    {
        Amount = Money.Thb(10000),
        ReferenceId = "order-1",
        AllowedMethods = [PaymentMethod.QrPromptPay],
    }));

    Assert.Single(handler.Requests);
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Themia.Payments.Beam.Tests --filter "BeamGetChargeTests|BeamRetryTests"`
Expected: FAIL.

- [ ] **Step 3: Implement**

Add to `BeamMapping`:

```csharp
public static PaymentStatus ToStatus(string status) => status switch
{
    "SUCCEEDED" => PaymentStatus.Succeeded,
    "FAILED" => PaymentStatus.Failed,
    _ => PaymentStatus.Pending,
};

public static PaymentFailure? ToFailure(string? failureCode, string? message)
{
    if (string.IsNullOrWhiteSpace(failureCode))
    {
        return null;
    }

    var reason = failureCode switch
    {
        "CH_INSUFFICIENT_FUNDS" => FailureReason.InsufficientFunds,
        "CH_AUTHENTICATION_FAILED" => FailureReason.AuthenticationFailed,
        "CH_PROCESSING_FAILED" => FailureReason.ProcessingFailed,
        _ when failureCode.StartsWith("CH_CARD_", StringComparison.Ordinal) => FailureReason.Declined,
        _ => FailureReason.Unknown,
    };

    return new PaymentFailure(reason, failureCode, message);
}
```

`BeamHttp.SendWithRetryAsync` builds the key **once** (`request.IdempotencyKey ?? Guid.NewGuid().ToString("N")`), then loops at most 3 attempts, retrying only on `>=500`, `429` and `HttpRequestException`/`TaskCanceledException`, with `Task.Delay(TimeSpan.FromMilliseconds(200 * 2^attempt) + jitter)`. Each attempt builds a **fresh** `HttpRequestMessage` (an `HttpRequestMessage` cannot be resent) carrying that same key.

- [ ] **Step 4: Run, build, commit**

```bash
git add src/neutral/Themia.Payments.Beam tests/Themia.Payments.Beam.Tests
git commit -m "feat(payments-beam): read a charge, retry with a stable idempotency key"
```

---

### Task 9: Beam — refunds, with the partial-refund guard

**Files:**
- Modify: `src/neutral/Themia.Payments.Beam/BeamPaymentGateway.cs`
- Create: `tests/Themia.Payments.Beam.Tests/BeamRefundTests.cs`

**Interfaces:**
- Produces: `RefundAsync` over `POST /api/v1/refunds`.

This is Review Focus item 5.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task A_full_refund_posts_the_charge_id_and_returns_the_refund_id()
{
    var handler = new StubHandler().Enqueue(HttpStatusCode.OK, """{ "refundId": "re_1" }""");
    var gateway = BuildWith(handler);

    var refund = await gateway.RefundAsync(new RefundRequest(new ChargeRef("ch_1", "order-1"), Amount: null, Reason: "duplicate", IdempotencyKey: "r-1"));

    using var sent = JsonDocument.Parse(handler.Bodies[0]);
    Assert.Equal("ch_1", sent.RootElement.GetProperty("chargeId").GetString());
    Assert.False(sent.RootElement.TryGetProperty("amount", out _));
    Assert.Equal("re_1", refund.RefundId);
    Assert.Equal(PaymentStatus.Pending, refund.Status);
}

[Fact]
public async Task A_partial_refund_of_a_qr_charge_is_refused_before_the_refund_call()
{
    // The adapter reads the charge first, because only CARD charges can be refunded in part.
    var handler = new StubHandler().Enqueue(HttpStatusCode.OK, """
    { "chargeId": "ch_1", "referenceId": "order-1", "status": "SUCCEEDED", "currency": "THB", "amount": 10000,
      "paymentMethod": { "paymentMethodType": "QR_PROMPT_PAY" } }
    """);
    var gateway = BuildWith(handler);

    var ex = await Assert.ThrowsAsync<PaymentApiException>(() => gateway.RefundAsync(
        new RefundRequest(new ChargeRef("ch_1", "order-1"), Money.Thb(5000), Reason: null, IdempotencyKey: null)));

    Assert.Equal("partial_refund_unsupported", ex.ProviderCode);
    Assert.Single(handler.Requests);                       // the GET only; no refund was attempted
    Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
}

[Fact]
public async Task A_partial_refund_of_a_card_charge_sends_the_amount()
{
    var handler = new StubHandler()
        .Enqueue(HttpStatusCode.OK, """
        { "chargeId": "ch_1", "referenceId": "order-1", "status": "SUCCEEDED", "currency": "THB", "amount": 10000,
          "paymentMethod": { "paymentMethodType": "CARD" } }
        """)
        .Enqueue(HttpStatusCode.OK, """{ "refundId": "re_2" }""");
    var gateway = BuildWith(handler);

    await gateway.RefundAsync(new RefundRequest(new ChargeRef("ch_1", "order-1"), Money.Thb(5000), null, null));

    using var sent = JsonDocument.Parse(handler.Bodies[1]);
    Assert.Equal(5000, sent.RootElement.GetProperty("amount").GetInt64());
}
```

- [ ] **Step 2: Run and watch them fail.** Run: `dotnet test tests/Themia.Payments.Beam.Tests --filter BeamRefundTests`

- [ ] **Step 3: Implement.** In `RefundAsync`: when `request.Amount` is null, post `{chargeId, reason}` only. When it is set, `GET /api/v1/charges/{id}` first; if `paymentMethod.paymentMethodType` is not `CARD`, throw `PaymentApiException(FailureKind.Validation, "partial_refund_unsupported", 0, "Beam refunds only CARD charges in part; this charge was paid by {type}. Refund it in full or not at all.")`. Otherwise post `{chargeId, reason, amount}`.

- [ ] **Step 4: Run, build, commit**

```bash
git add src/neutral/Themia.Payments.Beam tests/Themia.Payments.Beam.Tests
git commit -m "feat(payments-beam): refunds, with the partial-refund guard"
```

---

### Task 10: Beam — webhook verifier and the published golden vector

**Files:**
- Create: `src/neutral/Themia.Payments.Beam/BeamWebhookVerifier.cs`
- Create: `tests/Themia.Payments.Beam.Tests/BeamWebhookVerifierTests.cs`, `Fixtures/beam-webhook-vector.json`

**Interfaces:**
- Produces: `BeamWebhookVerifier : IPaymentWebhookVerifier`.

This is Review Focus item 3.

- [ ] **Step 1: Save the vector byte-exactly**

Open `https://docs.beamcheckout.com/webhook-authentication`, section *Example Webhook Data Authentication*, and copy the **raw, unformatted** request body into `tests/Themia.Payments.Beam.Tests/Fixtures/beam-webhook-vector.json` **exactly as printed — no reformatting, no trailing newline**. The page's own note says a formatted body will not match. That body pairs with:

- `X-Beam-Signature`: `1XzWtJHZ9Y1tmjkA/XZUIn1ZHrUQp1d0Ms0oDQfJBto=`
- HMAC key: `KOFELguf5L1ltuDlkDHGUkPPnQhrgYYijTR4Fqh7APc=`

Add to the test csproj so the bytes survive the copy unchanged:

```xml
<ItemGroup>
  <None Update="Fixtures/beam-webhook-vector.json" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

- [ ] **Step 2: Write the failing tests**

```csharp
public class BeamWebhookVerifierTests
{
    private const string Key = "KOFELguf5L1ltuDlkDHGUkPPnQhrgYYijTR4Fqh7APc=";
    private const string Signature = "1XzWtJHZ9Y1tmjkA/XZUIn1ZHrUQp1d0Ms0oDQfJBto=";

    private static byte[] Vector() => File.ReadAllBytes("Fixtures/beam-webhook-vector.json");

    private static BeamWebhookVerifier Verifier() =>
        new(Options.Create(new BeamOptions { MerchantId = "m", ApiKey = "k", WebhookHmacKey = Key }));

    private static Dictionary<string, string> Headers(string signature, string eventName = "charge.succeeded") =>
        new(StringComparer.OrdinalIgnoreCase) { ["X-Beam-Signature"] = signature, ["X-Beam-Event"] = eventName };

    [Fact]
    public void The_published_vector_verifies()
    {
        var result = Verifier().Verify(Vector(), Headers(Signature));

        Assert.Equal(WebhookOutcome.Verified, result.Outcome);
        Assert.Equal(PaymentEventType.ChargeSucceeded, result.Event!.Type);
        Assert.Equal("order#10001", result.Event.ReferenceId);
        Assert.Equal(Money.Thb(3000000), result.Event.Amount);
    }

    [Fact]
    public void One_changed_byte_in_the_body_fails()
    {
        var tampered = Vector();
        tampered[^2] = (byte)(tampered[^2] ^ 0x01);

        Assert.Equal(WebhookOutcome.SignatureMismatch, Verifier().Verify(tampered, Headers(Signature)).Outcome);
    }

    [Fact]
    public void A_body_that_was_parsed_and_re_serialized_no_longer_verifies()
    {
        // The trap this interface takes bytes to avoid: System.Text.Json round-tripping changes whitespace,
        // so a pipeline that binds the model before verifying will reject every real webhook.
        using var document = JsonDocument.Parse(Vector());
        var reserialized = JsonSerializer.SerializeToUtf8Bytes(document.RootElement);

        Assert.Equal(WebhookOutcome.SignatureMismatch, Verifier().Verify(reserialized, Headers(Signature)).Outcome);
    }

    [Fact]
    public void A_missing_signature_header_is_its_own_outcome()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Beam-Event"] = "charge.succeeded" };

        Assert.Equal(WebhookOutcome.SignatureMissing, Verifier().Verify(Vector(), headers).Outcome);
    }

    [Fact]
    public void An_event_this_package_does_not_model_is_authentic_but_typed_as_other()
    {
        var result = Verifier().Verify(Vector(), Headers(Signature, "bolt_intent.paid"));

        Assert.Equal(WebhookOutcome.Verified, result.Outcome);
        Assert.Equal(PaymentEventType.Other, result.Event!.Type);
    }
}
```

- [ ] **Step 3: Run and watch them fail.** Run: `dotnet test tests/Themia.Payments.Beam.Tests --filter BeamWebhookVerifierTests`

- [ ] **Step 4: Implement**

```csharp
public WebhookVerification Verify(ReadOnlySpan<byte> rawBody, IReadOnlyDictionary<string, string> headers)
{
    ArgumentNullException.ThrowIfNull(headers);

    if (!TryGet(headers, "X-Beam-Signature", out var provided) || string.IsNullOrWhiteSpace(provided))
    {
        return new WebhookVerification(WebhookOutcome.SignatureMissing, null);
    }

    var key = options.Value.WebhookHmacKey
        ?? throw new InvalidOperationException("BeamOptions.WebhookHmacKey must be set to verify webhooks.");

    Span<byte> computed = stackalloc byte[32];
    HMACSHA256.HashData(Convert.FromBase64String(key), rawBody, computed);

    Span<byte> presented = stackalloc byte[32];
    if (!Convert.TryFromBase64String(provided, presented, out var written)
        || written != computed.Length
        || !CryptographicOperations.FixedTimeEquals(computed, presented))
    {
        return new WebhookVerification(WebhookOutcome.SignatureMismatch, null);
    }

    // …parse the body, map X-Beam-Event to PaymentEventType (unknown names map to Other), and build the event.
}
```

Map the event names: `charge.succeeded` → `ChargeSucceeded`, `charge.failed` → `ChargeFailed`, `refund.succeeded` → `RefundSucceeded`, `refund.failed` → `RefundFailed`, anything else → `Other`. A body that will not parse is `Malformed`.

- [ ] **Step 5: Run, build, commit**

```bash
git add src/neutral/Themia.Payments.Beam tests/Themia.Payments.Beam.Tests
git commit -m "feat(payments-beam): webhook verifier pinned to Beam's published vector"
```

---

### Task 11: Beam-only surface — payment links and QR slip verification

**Files:**
- Create: `src/neutral/Themia.Payments.Beam/BeamPaymentClient.cs`
- Create: `tests/Themia.Payments.Beam.Tests/BeamPaymentClientTests.cs`

**Interfaces:**
- Produces: `BeamPaymentClient` with `CreatePaymentLinkAsync`, `GetPaymentLinkAsync`, `DisablePaymentLinkAsync`, `VerifyQrSlipAsync(Stream image, string fileName, …)` and `VerifyQrSlipAsync(string rawQrContent, …)`, returning `BeamSlipVerification(string ChargeId, BeamSlipVerificationResult Result)` where the enum is `UpdatedToSucceeded` / `AlreadySucceeded`.

These are on the concrete client, not on `IPaymentGateway`, because no second provider offers them.

- [ ] **Step 1: Write the failing tests** — one for a link create posting to `/api/v1/payment-links`, one for `PATCH /api/v1/payment-links/{id}/disable`, and three for slip verification:

```csharp
[Fact]
public async Task Slip_verification_sends_multipart_with_the_raw_qr_content()
{
    var handler = new StubHandler().Enqueue(HttpStatusCode.OK,
        """{ "chargeId": "ch_1", "verificationResult": "UPDATED_TO_SUCCEEDED" }""");
    var client = BuildClient(handler);

    var result = await client.VerifyQrSlipAsync("0041000600000101030040220015077082818AQR086795102TH9104FF93");

    Assert.Equal("/api/v1/charges/verify-qr-slip", handler.Requests[0].RequestUri!.AbsolutePath);
    Assert.Contains("form-data; name=\"format\"", handler.Bodies[0], StringComparison.Ordinal);
    Assert.Contains("RAW", handler.Bodies[0], StringComparison.Ordinal);
    Assert.Equal(BeamSlipVerificationResult.UpdatedToSucceeded, result.Result);
}

[Fact]
public async Task A_slip_matching_no_charge_is_a_not_found_failure_not_an_unverified_result()
{
    // Beam's verificationResult never reports failure; the HTTP status is the answer.
    var handler = new StubHandler().Enqueue(HttpStatusCode.NotFound,
        """{ "code": 404, "message": "not found", "error": { "errorCode": "NOT_FOUND_ERROR" } }""");
    var client = BuildClient(handler);

    var ex = await Assert.ThrowsAsync<PaymentApiException>(() => client.VerifyQrSlipAsync("0041"));

    Assert.Equal(FailureKind.NotFound, ex.Kind);
}
```

- [ ] **Step 2: Run and watch them fail.**

- [ ] **Step 3: Implement**

```csharp
namespace Themia.Payments.Beam;

/// <summary>Which of the two success cases a slip verification hit.</summary>
public enum BeamSlipVerificationResult
{
    /// <summary>The charge was PENDING and is now SUCCEEDED. Beam has sent the usual webhooks.</summary>
    UpdatedToSucceeded,

    /// <summary>The charge was already SUCCEEDED; nothing changed.</summary>
    AlreadySucceeded,
}

/// <summary>What a slip verification matched.</summary>
/// <param name="ChargeId">The charge the slip matched.</param>
/// <param name="Result">Which success case it was.</param>
public sealed record BeamSlipVerification(string ChargeId, BeamSlipVerificationResult Result);

/// <summary>Beam features with no counterpart in <see cref="IPaymentGateway"/>.</summary>
/// <remarks>
/// Reaching for this type is how an app says out loud that it is now tied to Beam. Two traps the wrappers
/// enforce: the QR to send is the one <b>on the slip</b>, not the one the shopper scanned to pay; and
/// <c>verificationResult</c> never reports failure — an unreadable or unmatched slip is a 4xx, so the HTTP
/// status is the answer. A slip can only match a charge <b>Beam itself created</b>, so this cannot confirm a
/// QR rendered offline by <c>Themia.PromptPay</c>.
/// </remarks>
public sealed class BeamPaymentClient
{
    /// <summary>Creates a hosted checkout link.</summary>
    public Task<BeamPaymentLink> CreatePaymentLinkAsync(BeamPaymentLinkRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reads a payment link's current state.</summary>
    public Task<BeamPaymentLink> GetPaymentLinkAsync(string paymentLinkId, CancellationToken cancellationToken = default);

    /// <summary>Stops an active link being paid. A link cannot be deleted or edited; this is the only mutation.</summary>
    public Task DisablePaymentLinkAsync(string paymentLinkId, CancellationToken cancellationToken = default);

    /// <summary>Verifies a slip whose QR content the caller already scanned (<c>format=RAW</c>).</summary>
    public Task<BeamSlipVerification> VerifyQrSlipAsync(string rawQrContent, CancellationToken cancellationToken = default);

    /// <summary>Verifies a slip image, letting Beam read the QR (<c>format=IMAGE</c>; JPEG or PNG, at most 2 MB).</summary>
    public Task<BeamSlipVerification> VerifyQrSlipAsync(Stream image, string fileName, CancellationToken cancellationToken = default);
}
```

Both slip overloads post `multipart/form-data` to `/api/v1/charges/verify-qr-slip` with a `format` part plus either `raw` or `image`, and translate a non-2xx through the same error mapping as Task 7.
- [ ] **Step 4: Run, build, commit**

```bash
git add src/neutral/Themia.Payments.Beam tests/Themia.Payments.Beam.Tests
git commit -m "feat(payments-beam): payment links and QR slip verification"
```

---

### Task 12: 2C2P — project and the JWT HS256 helper

**Files:**
- Create: `src/neutral/Themia.Payments.TwoCTwoP/Themia.Payments.TwoCTwoP.csproj`, `Internal/JwtHs256.cs`, `TwoCTwoPEnvironment.cs`, `TwoCTwoPOptions.cs`, `PublicAPI.*.txt`
- Create: `tests/Themia.Payments.TwoCTwoP.Tests/…csproj`, `JwtHs256Tests.cs`
- Modify: `Themia.sln`

**Interfaces:**
- Produces: `internal static class JwtHs256` with `string Encode(IReadOnlyDictionary<string, object?> payload, string secret)`, `bool TryDecode(string token, string secret, out JsonElement payload)`, and `JsonElement DecodePayloadWithoutVerifying(string token)`.

No JWT library: HS256 encode and verify is base64url plus `HMACSHA256`, and a dependency here would reach every consumer of a `net8.0;net10.0` package for thirty lines.

- [ ] **Step 1: Write the failing tests**

```csharp
public class JwtHs256Tests
{
    // RFC 7515 Appendix A.1 — the published HS256 example, so the implementation is pinned to the standard
    // rather than to itself.
    private const string RfcKeyBase64Url =
        "AyM1SysPpbyDfgZld3umj1qzKObwVMkoqQ-EstJQLr_T-1qS0gZH75aKtMN3Yj0iPS4hcgUuTwjAzZr1Z9CAow";

    [Fact]
    public void Encode_then_decode_round_trips_the_claims()
    {
        var token = JwtHs256.Encode(new Dictionary<string, object?>
        {
            ["merchantID"] = "JT01",
            ["invoiceNo"] = "1523953661",
            ["amount"] = 1000.00m,
            ["currencyCode"] = "THB",
        }, "secret");

        Assert.True(JwtHs256.TryDecode(token, "secret", out var payload));
        Assert.Equal("JT01", payload.GetProperty("merchantID").GetString());
        Assert.Equal("1523953661", payload.GetProperty("invoiceNo").GetString());
    }

    [Fact]
    public void A_token_signed_with_another_secret_does_not_decode()
    {
        var token = JwtHs256.Encode(new Dictionary<string, object?> { ["invoiceNo"] = "1" }, "secret");

        Assert.False(JwtHs256.TryDecode(token, "other-secret", out _));
    }

    [Fact]
    public void A_tampered_payload_does_not_decode()
    {
        var token = JwtHs256.Encode(new Dictionary<string, object?> { ["amount"] = 100m }, "secret");
        var parts = token.Split('.');
        var tampered = $"{parts[0]}.{parts[1][..^2]}XY.{parts[2]}";

        Assert.False(JwtHs256.TryDecode(tampered, "secret", out _));
    }

    [Fact]
    public void The_header_is_alg_HS256_and_typ_JWT()
    {
        var token = JwtHs256.Encode(new Dictionary<string, object?> { ["a"] = 1 }, "secret");
        var header = JsonDocument.Parse(JwtHs256.Base64UrlDecode(token.Split('.')[0]));

        Assert.Equal("HS256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("JWT", header.RootElement.GetProperty("typ").GetString());
    }

    [Fact]
    public void Amounts_are_serialized_with_two_decimals_under_any_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");   // comma decimal separator
            var token = JwtHs256.Encode(new Dictionary<string, object?> { ["amount"] = 1000.5m }, "secret");

            Assert.True(JwtHs256.TryDecode(token, "secret", out var payload));
            Assert.Equal("1000.50", payload.GetProperty("amount").GetRawText().Trim('"'));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
```

- [ ] **Step 2: Run and watch them fail.**
- [ ] **Step 3: Implement** `JwtHs256` with `Base64UrlEncode`/`Base64UrlDecode` (`'+'`→`'-'`, `'/'`→`'_'`, strip `'='`), `HMACSHA256.HashData`, `CryptographicOperations.FixedTimeEquals` on verify, and a `JsonSerializerOptions` that writes `decimal` with `ToString("F2", CultureInfo.InvariantCulture)`.
- [ ] **Step 4: Run, build, commit**

```bash
git add src/neutral/Themia.Payments.TwoCTwoP tests/Themia.Payments.TwoCTwoP.Tests Themia.sln
git commit -m "feat(payments-2c2p): HS256 JWT transport helper"
```

---

### Task 13: 2C2P — create a charge and read it

**Files:**
- Create: `src/neutral/Themia.Payments.TwoCTwoP/TwoCTwoPPaymentGateway.cs`, `TwoCTwoPMapping.cs`, `Internal/TwoCTwoPHttp.cs`, `DependencyInjection/TwoCTwoPServiceCollectionExtensions.cs`
- Create: `tests/Themia.Payments.TwoCTwoP.Tests/TwoCTwoPGatewayTests.cs`

**Interfaces:**
- Produces: `TwoCTwoPPaymentGateway : IPaymentGateway, IPaymentGatewayCapabilities`, `AddThemiaPaymentsTwoCTwoP(...)`, `TwoCTwoPMapping.ToStatus(string respCode)` and `.ToFailure(string respCode, string? respDesc)`.

Endpoints: `POST {base}/payment/4.3/paymentToken` and `POST {base}/payment/4.3/paymentInquiry`, base `https://sandbox-pgw.2c2p.com` or `https://pgw.2c2p.com`. Both take and return `{"payload": "<jwt>"}`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task Creating_a_charge_sends_a_jwt_payload_and_returns_the_hosted_url()
{
    var handler = new StubHandler().Enqueue(HttpStatusCode.OK, ResponseEnvelope(new Dictionary<string, object?>
    {
        ["webPaymentUrl"] = "https://sandbox-pgw-ui.2c2p.com/payment/4.3/#/token/abc",
        ["paymentToken"] = "abc",
        ["respCode"] = "0000",
        ["respDesc"] = "Success",
    }));
    var gateway = BuildGateway(handler);

    var creation = await gateway.CreateChargeAsync(new CreateChargeRequest
    {
        Amount = Money.Thb(100000),
        ReferenceId = "order-1",
        AllowedMethods = [PaymentMethod.Card, PaymentMethod.QrPromptPay],
    });

    Assert.Equal("/payment/4.3/paymentToken", handler.Requests[0].RequestUri!.AbsolutePath);
    var sent = PayloadOf(handler.Bodies[0]);
    Assert.Equal("order-1", sent.GetProperty("invoiceNo").GetString());
    Assert.Equal("1000.00", sent.GetProperty("amount").GetRawText().Trim('"'));
    Assert.Equal(["CC", "QR"], sent.GetProperty("paymentChannel").EnumerateArray().Select(e => e.GetString()));
    Assert.Equal(new Uri("https://sandbox-pgw-ui.2c2p.com/payment/4.3/#/token/abc"),
        Assert.IsType<NextAction.Redirect>(creation.Action).Url);
    Assert.Equal("order-1", creation.ChargeId);              // 2C2P mints no id before payment
}

[Fact]
public async Task A_reference_longer_than_twenty_characters_is_refused_before_the_call()
{
    var handler = new StubHandler();
    var gateway = BuildGateway(handler);

    var ex = await Assert.ThrowsAsync<PaymentApiException>(() => gateway.CreateChargeAsync(new CreateChargeRequest
    {
        Amount = Money.Thb(1000),
        ReferenceId = new string('x', 21),
        AllowedMethods = [PaymentMethod.Card],
    }));

    Assert.Equal("reference_id_too_long", ex.ProviderCode);
    Assert.Empty(handler.Requests);
}

[Fact]
public async Task Reading_a_charge_inquires_by_the_apps_own_reference()
{
    var handler = new StubHandler().Enqueue(HttpStatusCode.OK, ResponseEnvelope(new Dictionary<string, object?>
    {
        ["invoiceNo"] = "order-1", ["amount"] = 1000.00m, ["currencyCode"] = "THB",
        ["respCode"] = "0000", ["respDesc"] = "Success", ["transactionDateTime"] = "2026-09-22T10:00:00",
    }));
    var gateway = BuildGateway(handler);

    var charge = await gateway.GetChargeAsync(new ChargeRef(ProviderChargeId: null, ReferenceId: "order-1"));

    Assert.Equal("/payment/4.3/paymentInquiry", handler.Requests[0].RequestUri!.AbsolutePath);
    Assert.Equal("order-1", PayloadOf(handler.Bodies[0]).GetProperty("invoiceNo").GetString());
    Assert.Equal(PaymentStatus.Succeeded, charge.Status);
    Assert.Equal(Money.Thb(100000), charge.Amount);          // 1000.00 THB back into minor units
}

[Theory]
[InlineData("0001", PaymentStatus.Pending, FailureReason.Unknown)]
[InlineData("2001", PaymentStatus.Pending, FailureReason.Unknown)]
[InlineData("0003", PaymentStatus.Failed, FailureReason.Canceled)]
[InlineData("0004", PaymentStatus.Failed, FailureReason.AuthenticationFailed)]
[InlineData("2003", PaymentStatus.Failed, FailureReason.ProcessingFailed)]
[InlineData("0999", PaymentStatus.Failed, FailureReason.ProcessingFailed)]
[InlineData("4051", PaymentStatus.Failed, FailureReason.InsufficientFunds)]
[InlineData("4005", PaymentStatus.Failed, FailureReason.Declined)]
public void Response_codes_map_to_a_status_and_a_reason(string respCode, PaymentStatus status, FailureReason reason)
{
    Assert.Equal(status, TwoCTwoPMapping.ToStatus(respCode));
    if (status == PaymentStatus.Failed)
    {
        Assert.Equal(reason, TwoCTwoPMapping.ToFailure(respCode, "desc")!.Reason);
        Assert.Equal(respCode, TwoCTwoPMapping.ToFailure(respCode, "desc")!.ProviderCode);
    }
}

[Fact]
public async Task A_transaction_not_found_becomes_a_not_found_exception()
{
    var handler = new StubHandler().Enqueue(HttpStatusCode.OK, ResponseEnvelope(new Dictionary<string, object?>
    {
        ["respCode"] = "2002", ["respDesc"] = "Transaction not found",
    }));
    var gateway = BuildGateway(handler);

    var ex = await Assert.ThrowsAsync<PaymentApiException>(
        () => gateway.GetChargeAsync(new ChargeRef(null, "order-missing")));

    Assert.Equal(FailureKind.NotFound, ex.Kind);
    Assert.Equal("2002", ex.ProviderCode);
}
```

- [ ] **Step 2: Run and watch them fail.**
- [ ] **Step 3: Implement.** Method mapping to `paymentChannel`: `Card` → `"CC"`, `QrPromptPay` → `"QR"`, `MobileBanking` → `"MB"`, `Wallet` → `"EW"`; `SupportedMethods` lists all four. `ChargeId` is the invoice number, because 2C2P has no id before payment. Amount conversion: `MinorUnits / 100m` formatted `F2` invariant on the way out, `decimal.Parse(...) * 100` rounded to a `long` on the way back.
- [ ] **Step 4: Run, build, commit**

```bash
git add src/neutral/Themia.Payments.TwoCTwoP tests/Themia.Payments.TwoCTwoP.Tests
git commit -m "feat(payments-2c2p): create and read a charge over PGW 4.3"
```

---

### Task 14: 2C2P — refund, and the backend-notification verifier

**Files:**
- Modify: `src/neutral/Themia.Payments.TwoCTwoP/TwoCTwoPPaymentGateway.cs`
- Create: `src/neutral/Themia.Payments.TwoCTwoP/TwoCTwoPWebhookVerifier.cs`
- Create: `tests/Themia.Payments.TwoCTwoP.Tests/TwoCTwoPRefundTests.cs`, `TwoCTwoPWebhookVerifierTests.cs`

**Interfaces:**
- Produces: `RefundAsync` over the Payment Process API, and `TwoCTwoPWebhookVerifier : IPaymentWebhookVerifier`.

- [ ] **Step 1: Confirm the refund endpoint before writing it**

Read `https://developer.2c2p.com/docs/api-payment-action-payment-process.md`. It documents an **XML** request (`<PaymentProcessRequest>` with `version`, `timeStamp`, `merchantID`, `processType`, `invoiceNo`, `actionAmount`, `idempotencyID`) while the rest of PGW 4.3 is JWT-over-JSON. Copy the endpoint path and the `processType` value for a refund from that page into the implementation — **do not infer either**. If the page does not state the path, stop and ask rather than guessing: a wrong path here fails in production, not in tests.

- [ ] **Step 2: Write the failing tests**

```csharp
[Fact]
public async Task A_refund_sends_the_invoice_number_the_amount_and_an_idempotency_id()
{
    var handler = new StubHandler().Enqueue(HttpStatusCode.OK, "<PaymentProcessResponse><respCode>0000</respCode></PaymentProcessResponse>");
    var gateway = BuildGateway(handler);

    var refund = await gateway.RefundAsync(new RefundRequest(
        new ChargeRef(null, "order-1"), Money.Thb(50000), "customer changed their mind", "r-1"));

    var body = handler.Bodies[0];
    Assert.Contains("<invoiceNo>order-1</invoiceNo>", body, StringComparison.Ordinal);
    Assert.Contains("<actionAmount>500.00</actionAmount>", body, StringComparison.Ordinal);
    Assert.Contains("<idempotencyID>r-1</idempotencyID>", body, StringComparison.Ordinal);
    Assert.Null(refund.RefundId);                               // 2C2P mints no refund id
    Assert.Equal(PaymentStatus.Pending, refund.Status);
}

[Fact]
public void A_backend_notification_verifies_by_its_own_jwt_signature_with_no_header()
{
    var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        payload = JwtHs256.Encode(new Dictionary<string, object?>
        {
            ["invoiceNo"] = "order-1", ["amount"] = 1000.00m, ["currencyCode"] = "THB",
            ["respCode"] = "0000", ["respDesc"] = "Success",
        }, "secret"),
    }));

    var result = Verifier("secret").Verify(body, new Dictionary<string, string>());

    Assert.Equal(WebhookOutcome.Verified, result.Outcome);
    Assert.Equal(PaymentEventType.ChargeSucceeded, result.Event!.Type);
    Assert.Equal("order-1", result.Event.ReferenceId);
}

[Fact]
public void A_notification_signed_with_another_secret_is_a_mismatch()
{
    var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        payload = JwtHs256.Encode(new Dictionary<string, object?> { ["invoiceNo"] = "order-1", ["respCode"] = "0000" }, "attacker"),
    }));

    Assert.Equal(WebhookOutcome.SignatureMismatch, Verifier("secret").Verify(body, new Dictionary<string, string>()).Outcome);
}
```

- [ ] **Step 3: Run and watch them fail.**
- [ ] **Step 4: Implement.** The verifier reads `{"payload": "<jwt>"}`, verifies with the merchant secret, and maps `respCode` through `TwoCTwoPMapping`. A body that is not JSON, or has no `payload`, is `Malformed`.
- [ ] **Step 5: Run, build, commit**

```bash
git add src/neutral/Themia.Payments.TwoCTwoP tests/Themia.Payments.TwoCTwoP.Tests
git commit -m "feat(payments-2c2p): refunds and the backend-notification verifier"
```

---

### Task 15: One contract suite, run against both adapters

**Files:**
- Create: `tests/Themia.Payments.ContractTests/Themia.Payments.ContractTests.csproj`, `PaymentGatewayContract.cs`, `BeamContractTests.cs`, `TwoCTwoPContractTests.cs`
- Modify: `Themia.sln`

**Interfaces:**
- Consumes: both adapters and their stub handlers.
- Produces: `public abstract class PaymentGatewayContract` with abstract `IPaymentGateway CreateGateway(StubHandler handler)` and `void ScriptCreateSuccess(StubHandler handler)` etc., plus one concrete subclass per adapter.

A change that fits only one provider fails here. This is the test that keeps the core from drifting back into Beam's shape.

- [ ] **Step 1: Write the abstract suite**

```csharp
public abstract class PaymentGatewayContract
{
    protected abstract IPaymentGateway CreateGateway(StubHandler handler, PaymentMethodPolicy? policy = null);

    protected abstract void ScriptChargeCreated(StubHandler handler);

    protected abstract void ScriptChargeSucceeded(StubHandler handler);

    [Fact]
    public async Task Creating_a_charge_returns_a_pending_charge_with_a_next_action()
    {
        var handler = new StubHandler();
        ScriptChargeCreated(handler);

        var creation = await CreateGateway(handler).CreateChargeAsync(Request());

        Assert.Equal(PaymentStatus.Pending, creation.Status);
        Assert.NotNull(creation.ChargeId);
        Assert.IsNotType<NextAction.None>(creation.Action);
    }

    [Fact]
    public async Task A_zero_amount_never_reaches_the_provider()
    {
        var handler = new StubHandler();

        await Assert.ThrowsAsync<PaymentApiException>(
            () => CreateGateway(handler).CreateChargeAsync(Request() with { Amount = Money.Thb(0) }));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_method_policy_is_honoured_by_every_adapter()
    {
        var handler = new StubHandler();
        var policy = new PaymentMethodPolicy
        {
            Currency = "THB",
            Bands = [new PaymentMethodBand(100000, [PaymentMethod.QrPromptPay])],
            Above = [PaymentMethod.QrPromptPay, PaymentMethod.Card],
        };

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => CreateGateway(handler, policy)
            .CreateChargeAsync(Request() with { Amount = Money.Thb(50000), AllowedMethods = [PaymentMethod.Card] }));

        Assert.Equal("method_not_allowed_for_amount", ex.ProviderCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_succeeded_charge_reads_back_with_its_amount_and_no_failure()
    {
        var handler = new StubHandler();
        ScriptChargeSucceeded(handler);

        var charge = await CreateGateway(handler).GetChargeAsync(new ChargeRef("ch_1", "order-1"));

        Assert.Equal(PaymentStatus.Succeeded, charge.Status);
        Assert.Null(charge.Failure);
        Assert.Equal(Money.Thb(100000), charge.Amount);
    }

    private static CreateChargeRequest Request() => new()
    {
        Amount = Money.Thb(100000),
        ReferenceId = "order-1",
        AllowedMethods = [PaymentMethod.QrPromptPay],
    };
}
```

- [ ] **Step 2: Implement the two subclasses**, each scripting its own provider's response bodies (reuse the JSON already written in Tasks 7 and 13).
- [ ] **Step 3: Run and fix whatever only one adapter satisfies.** Run: `dotnet test tests/Themia.Payments.ContractTests`
- [ ] **Step 4: Commit**

```bash
git add tests/Themia.Payments.ContractTests Themia.sln
git commit -m "test(payments): one contract suite over both adapters"
```

---

### Task 16: READMEs, changelog, catalog rows, and the release

**Files:**
- Create: `src/neutral/Themia.Payments/README.md`, `src/neutral/Themia.Payments.Beam/README.md`, `src/neutral/Themia.Payments.TwoCTwoP/README.md`
- Modify: `CHANGELOG.md`, `docs/themia-architecture-overview.md` (§B catalog rows and the Specs index marker), `Directory.Build.props`

- [ ] **Step 1: Write the three READMEs.** Each states what the package is, the smallest working registration, and the traps a reader must know before using it — for the core: `Pending` can last for ever, the webhook signature proves origin and not freshness, partial refunds are card-only, and every charge must be the platform's own revenue. For Beam: the environments, that `MobileBanking`/`Wallet` are payment-link-only, and that slip verification matches only Beam's own charges. For 2C2P: that the JWT signature *is* the authentication, the 20-character `invoiceNo` cap, and that refunds go through an XML API.

- [ ] **Step 2: Add the catalog rows.** In `docs/themia-architecture-overview.md` §B add `Themia.Payments`, `Themia.Payments.Beam` and `Themia.Payments.TwoCTwoP` with their status and release, and change the Specs index line for `2026-09-22-themia-payments-design.md` from ⬜ to ✅ with `(0.30.0)`.

- [ ] **Step 3: Write the changelog entry** under `## [Unreleased]` → `### Added`, naming the three packages, the method policy, and the two adapters' quirks that a reader would otherwise hit (`Pending` never resolving, replayable webhooks, card-only partial refunds).

- [ ] **Step 4: Run the whole suite and a clean build**

```bash
dotnet build Themia.sln --no-incremental
dotnet test Themia.sln
```

Expected: 0 errors, no `RS0016`, every test green.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "docs(payments): READMEs, catalog rows and changelog for 0.30.0"
```

---

## Notes for the executor

- **Do not add a `Themia.Modules.Payments`.** Nothing here is tenant-scoped or persistent, and the one thing that would justify a module — sub-merchant split payment — is out of scope by the consumers' own answers (spec §1, §8).
- **Do not ship a "wait until final" helper**, however convenient it looks. A charge can stay `Pending` for ever, and a loop that waits for a final status is a hang waiting for an adopter to inherit.
- **Do not let an adapter's DTO escape into the core.** If a provider field has no home in the core's records, it belongs on that adapter's own type or in `PaymentEvent.RawJson`.
- When a provider's documentation and this plan disagree, the documentation wins — and say so in the commit message, so the spec can be corrected rather than quietly diverging.
