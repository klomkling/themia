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

    /// <summary>
    /// Authentic but unrecognised event name. <b>Reserved:</b> no current adapter produces it — Beam and 2C2P
    /// both return <see cref="Verified"/> with <see cref="PaymentEventType.Other"/> for an authentic event they
    /// do not model. Handle it anyway, as a future adapter may.
    /// </summary>
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
