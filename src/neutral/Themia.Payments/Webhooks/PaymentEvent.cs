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
/// <param name="RawJson">
/// The provider's own JSON, for anything provider-specific: Beam carries the raw request body as received;
/// 2C2P carries the decoded, verified JWT payload (the body itself is only an envelope around the token).
/// It can hold customer personal data (names, phone numbers, e-mail, card last four) — and so can this
/// record's generated <c>ToString()</c>, which includes it. Do not log either.
/// </param>
public sealed record PaymentEvent(
    PaymentEventType Type,
    string? ChargeId,
    string? ReferenceId,
    Money? Amount,
    PaymentStatus Status,
    DateTimeOffset OccurredAt,
    string RawJson);
