namespace Themia.Payments;

/// <summary>The outcome of verifying a webhook, and the event when it was authentic.</summary>
/// <param name="Outcome">What the check concluded.</param>
/// <param name="Event">The event, when <paramref name="Outcome"/> is <see cref="WebhookOutcome.Verified"/>.</param>
public sealed record WebhookVerification(WebhookOutcome Outcome, PaymentEvent? Event);
