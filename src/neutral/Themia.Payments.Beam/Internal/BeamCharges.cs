using System.Net;
using System.Text.Json;

namespace Themia.Payments.Beam.Internal;

/// <summary>
/// Beam's charge-reading calls: by id, by payment-link id, and by the app's own reference id. Lives here, not
/// inline in <see cref="BeamPaymentGateway"/>, so the gateway's reads and refunds share one resolution path.
/// </summary>
internal static class BeamCharges
{
    /// <summary>
    /// Resolves a <see cref="ChargeRef"/> in three steps: a provider id reads the charge directly; a 404 there is
    /// retried as a payment link id, following a paid link to the charge that paid it; with no provider id at
    /// all, the app's reference id is looked up instead. <c>IsUnpaidLink</c> is true when the id named a link
    /// that no charge has paid yet — the returned <see cref="Charge"/> then describes the link itself.
    /// </summary>
    public static async Task<(Charge Charge, bool IsUnpaidLink)> ResolveAsync(
        HttpClient httpClient, BeamOptions beamOptions, ChargeRef charge, CancellationToken cancellationToken)
    {
        if (charge.ProviderChargeId is not { Length: > 0 } id)
        {
            return (await GetByReferenceAsync(httpClient, beamOptions, charge.ReferenceId, cancellationToken)
                .ConfigureAwait(false), false);
        }

        var (found, byId) = await TryGetByIdAsync(httpClient, beamOptions, id, cancellationToken).ConfigureAwait(false);
        if (found)
        {
            return (byId!, false);
        }

        // A paid link resolves to the charge that paid it, which has its own id; an unpaid link describes itself.
        var viaLink = await GetByPaymentLinkIdAsync(httpClient, beamOptions, id, cancellationToken).ConfigureAwait(false);
        return (viaLink, string.Equals(viaLink.ChargeId, id, StringComparison.Ordinal));
    }

    /// <summary>Reads a charge by its own id. A 404 is reported as not-found rather than thrown, since the id
    /// may still name a payment link.</summary>
    public static async Task<(bool Found, Charge? Charge)> TryGetByIdAsync(
        HttpClient httpClient, BeamOptions beamOptions, string chargeId, CancellationToken cancellationToken)
    {
        var (statusCode, parsed, root) = await GetChargeByIdAsync(httpClient, beamOptions, chargeId, cancellationToken)
            .ConfigureAwait(false);
        if (statusCode == HttpStatusCode.NotFound)
        {
            return (false, null);
        }

        if (!IsSuccess(statusCode))
        {
            throw BeamMapping.ToApiException(statusCode, parsed ? root : null);
        }

        return (true, ToCharge(root, (int)statusCode));
    }

    /// <summary>
    /// Reads a charge by id together with its <c>paymentMethod.paymentMethodType</c> — the only caller that
    /// needs the method type is the refund partial-amount guard, so it is not carried on the shared
    /// <see cref="Charge"/> type. Reuses <see cref="ToCharge"/> rather than parsing the body twice. Unlike
    /// <see cref="TryGetByIdAsync"/>, a 404 here is not special-cased — it throws like any other non-2xx,
    /// because by the time a refund needs this, the charge id is already known to exist.
    /// </summary>
    public static async Task<(Charge Charge, string? PaymentMethodType)> GetByIdWithPaymentMethodAsync(
        HttpClient httpClient, BeamOptions beamOptions, string chargeId, CancellationToken cancellationToken)
    {
        var (statusCode, parsed, root) = await GetChargeByIdAsync(httpClient, beamOptions, chargeId, cancellationToken)
            .ConfigureAwait(false);
        if (!IsSuccess(statusCode))
        {
            throw BeamMapping.ToApiException(statusCode, parsed ? root : null);
        }

        var charge = ToCharge(root, (int)statusCode);
        var paymentMethodType = root.TryGetProperty("paymentMethod", out var paymentMethod) &&
            BeamMapping.TryGetNonEmptyString(paymentMethod, "paymentMethodType", out var type)
                ? type
                : null;

        return (charge, paymentMethodType);
    }

    /// <summary>
    /// GET <c>/api/v1/charges/{id}</c> through the retry wrapper (no idempotency key) and parses the body,
    /// leaving the not-found and success/failure decisions to the caller — <see cref="TryGetByIdAsync"/> and
    /// <see cref="GetByIdWithPaymentMethodAsync"/> differ only in what they do with a 404. The returned
    /// <see cref="JsonElement"/> is safe to use after this method returns: <see cref="BeamMapping.TryParseAsync"/>
    /// clones it from its own <see cref="JsonDocument"/> before that document is disposed.
    /// </summary>
    private static async Task<(HttpStatusCode StatusCode, bool Parsed, JsonElement Root)> GetChargeByIdAsync(
        HttpClient httpClient, BeamOptions beamOptions, string chargeId, CancellationToken cancellationToken)
    {
        using var response = await BeamHttp.SendWithRetryAsync(
            httpClient,
            HttpMethod.Get,
            $"/api/v1/charges/{Uri.EscapeDataString(chargeId)}",
            beamOptions,
            contentFactory: null,
            idempotencyKey: null,
            cancellationToken).ConfigureAwait(false);

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var (parsed, root) = await BeamMapping.TryParseAsync(stream, cancellationToken).ConfigureAwait(false);
            return (response.StatusCode, parsed, root);
        }
    }

    /// <summary>Whether an HTTP status is a 2xx — the same check <see cref="HttpResponseMessage.IsSuccessStatusCode"/>
    /// makes, kept here so <see cref="GetChargeByIdAsync"/>'s callers can apply it after the response is disposed.</summary>
    private static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and < 300;

    /// <summary>Follows a payment-link id to what it says about the charge that paid it, or its own pending/failed state.</summary>
    public static async Task<Charge> GetByPaymentLinkIdAsync(
        HttpClient httpClient, BeamOptions beamOptions, string linkId, CancellationToken cancellationToken)
    {
        var link = await BeamPaymentLinks.GetAsync(httpClient, beamOptions, linkId, cancellationToken).ConfigureAwait(false);
        var amount = link.Amount;

        return link.Status switch
        {
            "ACTIVE" => new Charge(linkId, link.ReferenceId, amount, PaymentStatus.Pending, null, null),
            "PAID" or "VOIDED" or "REFUNDED" => await GetPaidChargeForLinkAsync(httpClient, beamOptions, linkId, cancellationToken)
                .ConfigureAwait(false),
            "EXPIRED" => new Charge(linkId, link.ReferenceId, amount, PaymentStatus.Failed,
                new PaymentFailure(FailureReason.Expired, link.Status, null), null),
            "DISABLED" => new Charge(linkId, link.ReferenceId, amount, PaymentStatus.Failed,
                new PaymentFailure(FailureReason.Canceled, link.Status, null), null),
            _ => throw new PaymentApiException(FailureKind.Unknown, link.Status, (int)HttpStatusCode.OK),
        };
    }

    /// <summary>Looks a charge up by the app's own reference id, preferring the one that succeeded.</summary>
    public static async Task<Charge> GetByReferenceAsync(
        HttpClient httpClient, BeamOptions beamOptions, string referenceId, CancellationToken cancellationToken)
    {
        var path = $"/api/v1/charges?referenceId={Uri.EscapeDataString(referenceId)}";
        var charges = await ListChargesAsync(httpClient, beamOptions, path, cancellationToken).ConfigureAwait(false);
        if (charges.Count == 0)
        {
            throw new PaymentApiException(FailureKind.NotFound, "no_charge_for_reference", 0);
        }

        foreach (var element in charges)
        {
            if (BeamMapping.TryGetNonEmptyString(element, "status", out var status) && status == "SUCCEEDED")
            {
                return ToCharge(element, (int)HttpStatusCode.OK);
            }
        }

        // None succeeded yet: charges list most-recent-first, so the first entry is the most recent attempt.
        return ToCharge(charges[0], (int)HttpStatusCode.OK);
    }

    /// <summary>The charge that paid a link, once it has one: the first <c>SUCCEEDED</c> charge sourced from it.</summary>
    private static async Task<Charge> GetPaidChargeForLinkAsync(
        HttpClient httpClient, BeamOptions beamOptions, string linkId, CancellationToken cancellationToken)
    {
        var path = $"/api/v1/charges?source_in=PAYMENT_LINK&sourceId={Uri.EscapeDataString(linkId)}";
        var charges = await ListChargesAsync(httpClient, beamOptions, path, cancellationToken).ConfigureAwait(false);
        foreach (var element in charges)
        {
            if (BeamMapping.TryGetNonEmptyString(element, "status", out var status) && status == "SUCCEEDED")
            {
                return ToCharge(element, (int)HttpStatusCode.OK);
            }
        }

        throw new PaymentApiException(FailureKind.NotFound, "no_charge_for_link", 0);
    }

    /// <summary>Calls a charge-list endpoint (<c>{ "data": Charge[], "totalCount": n }</c>) and returns the entries.</summary>
    private static async Task<List<JsonElement>> ListChargesAsync(
        HttpClient httpClient, BeamOptions beamOptions, string path, CancellationToken cancellationToken)
    {
        using var response = await BeamHttp.SendWithRetryAsync(
            httpClient, HttpMethod.Get, path, beamOptions, contentFactory: null, idempotencyKey: null, cancellationToken)
            .ConfigureAwait(false);

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var (parsed, root) = await BeamMapping.TryParseAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw BeamMapping.ToApiException(response.StatusCode, parsed ? root : null);
            }

            if (!parsed || root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                throw new PaymentApiException(FailureKind.Unknown, "malformed_response", (int)response.StatusCode);
            }

            var charges = new List<JsonElement>();
            foreach (var element in data.EnumerateArray())
            {
                charges.Add(element);
            }

            return charges;
        }
    }

    /// <summary>Maps a Beam <c>Charge</c> object to <see cref="Charge"/>.</summary>
    private static Charge ToCharge(JsonElement chargeElement, int httpStatus)
    {
        if (!BeamMapping.TryGetNonEmptyString(chargeElement, "chargeId", out var chargeId) ||
            !BeamMapping.TryGetNonEmptyString(chargeElement, "referenceId", out var referenceId) ||
            !BeamMapping.TryGetNonEmptyString(chargeElement, "currency", out var currency) ||
            !chargeElement.TryGetProperty("amount", out var amountElement) ||
            amountElement.ValueKind != JsonValueKind.Number ||
            !amountElement.TryGetInt64(out var amountMinorUnits) ||
            !BeamMapping.TryMoney(amountMinorUnits, currency, out var amount) ||
            !BeamMapping.TryGetNonEmptyString(chargeElement, "status", out var statusText))
        {
            throw new PaymentApiException(FailureKind.Unknown, "malformed_response", httpStatus);
        }

        var status = BeamMapping.ToStatus(statusText);
        var failureCode = chargeElement.TryGetProperty("failureCode", out var failureCodeElement) &&
            failureCodeElement.ValueKind == JsonValueKind.String
                ? failureCodeElement.GetString()
                : null;

        DateTimeOffset? completedAt = null;
        if (status != PaymentStatus.Pending &&
            chargeElement.TryGetProperty("transactionTime", out var transactionTimeElement) &&
            transactionTimeElement.ValueKind == JsonValueKind.String)
        {
            if (!transactionTimeElement.TryGetDateTimeOffset(out var parsedTransactionTime))
            {
                throw new PaymentApiException(FailureKind.Unknown, "malformed_response", httpStatus);
            }

            completedAt = parsedTransactionTime;
        }

        return new Charge(
            chargeId,
            referenceId,
            amount,
            status,
            BeamMapping.ToFailure(failureCode, null),
            completedAt);
    }
}
