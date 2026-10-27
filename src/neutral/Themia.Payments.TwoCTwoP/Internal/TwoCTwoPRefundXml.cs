using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Themia.Payments.TwoCTwoP.Internal;

/// <summary>The Payment Process XML of a refund (processType R) and its response.</summary>
internal static class TwoCTwoPRefundXml
{
    private const string RequestVersion = "4.3";

    /// <summary>What a verified response says. Strings are as 2C2P sent them.</summary>
    public sealed record Response(string RespCode, string? RespDesc, string? Status, string? InvoiceNo, string? MerchantId, string? IdempotencyId);

    /// <summary>Builds the request with <see cref="XElement"/>, so every value is escaped. Amount is a two-place decimal, invariant culture.</summary>
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
        return root.ToString(SaveOptions.DisableFormatting);
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
