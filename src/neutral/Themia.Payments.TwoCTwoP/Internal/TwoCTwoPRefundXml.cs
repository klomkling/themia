using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Themia.Payments.TwoCTwoP.Internal;

/// <summary>The Payment Process XML of a refund (processType R), of a refund status inquiry (processType RS) and their responses.</summary>
internal static class TwoCTwoPRefundXml
{
    private const string RequestVersion = "4.3";

    /// <summary>
    /// The longest key sent as <c>userDefined1</c>. 2C2P documents 150 for <c>userDefined1</c> on a payment token request but publishes no
    /// length for the Payment Process request, and a rejected value would fail the whole refund, so the cap is deliberately low. A longer key is
    /// still sent as <c>idempotencyID</c>; it just cannot be matched back through the refund status.
    /// </summary>
    public const int MaxUserDefinedLength = 50;

    /// <summary>What a verified response says. Strings are as 2C2P sent them.</summary>
    public sealed record Response(string RespCode, string? RespDesc, string? Status, string? InvoiceNo, string? MerchantId, string? IdempotencyId);

    /// <summary>One refund in a refund status answer. <see cref="Amount"/> is the decimal 2C2P sent; <see cref="DateTime"/> is its bare <c>yyyyMMddHHmmss</c>.</summary>
    public sealed record RefundRow(decimal Amount, string? Status, string? ReferenceNo, string? DateTime, string? UserDefined1);

    /// <summary>What a verified refund status answer says. <see cref="Refunds"/> is empty when 2C2P lists none.</summary>
    public sealed record StatusResponse(string RespCode, string? RespDesc, string? InvoiceNo, string? MerchantId, IReadOnlyList<RefundRow> Refunds);

    /// <summary>Builds the request with <see cref="XElement"/>, so every value is escaped. Amount is a two-place decimal, invariant culture.
    /// The idempotency id is also sent as <c>userDefined1</c> when it is at most <see cref="MaxUserDefinedLength"/> characters; 2C2P echoes it on the refund in a status inquiry, so the refund can be matched later.</summary>
    public static string BuildRequest(string merchantId, string invoiceNo, decimal amount, string idempotencyId, DateTimeOffset now)
    {
        var root = new XElement("PaymentProcessRequest",
            new XElement("version", RequestVersion),
            new XElement("timeStamp", now.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture)),
            new XElement("merchantID", merchantId),
            new XElement("processType", "R"),
            new XElement("invoiceNo", invoiceNo),
            new XElement("actionAmount", amount.ToString("0.00", CultureInfo.InvariantCulture)),
            new XElement("idempotencyID", idempotencyId));
        if (idempotencyId.Length <= MaxUserDefinedLength)
        {
            root.Add(new XElement("userDefined1", idempotencyId));
        }

        return root.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>The refund status inquiry for an invoice: no amount, no idempotency id, nothing is changed by it.</summary>
    public static string BuildStatusRequest(string merchantId, string invoiceNo, DateTimeOffset now) =>
        new XElement("PaymentProcessRequest",
            new XElement("version", RequestVersion),
            new XElement("timeStamp", now.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture)),
            new XElement("merchantID", merchantId),
            new XElement("processType", "RS"),
            new XElement("invoiceNo", invoiceNo)).ToString(SaveOptions.DisableFormatting);

    /// <summary>Parses a refund status answer, DTDs prohibited. <see langword="null"/> when it is not a <c>PaymentProcessResponse</c> with a
    /// <c>respCode</c>, or when any listed refund has an unreadable amount (a row is never skipped silently).</summary>
    public static StatusResponse? ParseStatusResponse(string xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersFromEntities = 0 };
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            var root = XDocument.Load(reader).Root;
            if (root is not { Name.LocalName: "PaymentProcessResponse" } || Text(root, "respCode") is not { } respCode)
            {
                return null;
            }

            var rows = new List<RefundRow>();
            foreach (var refund in root.Element("refundList")?.Elements("refund") ?? [])
            {
                if (!decimal.TryParse(Attribute(refund, "amount"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
                {
                    return null;
                }

                rows.Add(new RefundRow(amount, Attribute(refund, "status"), Attribute(refund, "referenceNo"), Attribute(refund, "dateTime"), Attribute(refund, "userDefined1")));
            }

            return new StatusResponse(respCode, Text(root, "respDesc"), Text(root, "invoiceNo"), Text(root, "merchantID"), rows);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static string? Attribute(XElement element, string name)
    {
        var value = element.Attribute(name)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>Parses a response with DTDs prohibited and no resolver. <see langword="null"/> when it is not a <c>PaymentProcessResponse</c> with a <c>respCode</c>.</summary>
    public static Response? ParseResponse(string xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersFromEntities = 0 };
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            var root = XDocument.Load(reader).Root;
            if (root is not { Name.LocalName: "PaymentProcessResponse" })
            {
                return null;
            }

            var respCode = Text(root, "respCode");
            return respCode is null ? null : new Response(respCode, Text(root, "respDesc"), Text(root, "status"), Text(root, "invoiceNo"), Text(root, "merchantID"), Text(root, "idempotencyID"));
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static string? Text(XElement root, string name)
    {
        var value = root.Element(name)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
