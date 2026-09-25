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
