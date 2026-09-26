namespace Themia.Payments;

/// <summary>What a verified webhook said.</summary>
/// <param name="Type">
/// Which event. A provider may deliver this outside the signed body (e.g. as an unsigned header), so an
/// <see cref="IPaymentWebhookVerifier"/> implementation must reconcile it against the signed body before
/// trusting it — reporting <see cref="WebhookOutcome.Malformed"/>, not this type, when they disagree.
/// </param>
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
