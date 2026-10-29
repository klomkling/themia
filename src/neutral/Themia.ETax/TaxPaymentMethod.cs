using System.Text.Json.Serialization;

namespace Themia.ETax;

/// <summary>How a receipt was paid.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TaxPaymentMethod>))]
public enum TaxPaymentMethod
{
    /// <summary>Paid in cash (เงินสด).</summary>
    Cash = 1,

    /// <summary>Paid by bank transfer (โอนเงิน).</summary>
    Transfer = 2,

    /// <summary>Paid by credit or debit card (บัตร). Printable on template versions "6" and "7" only.</summary>
    Card = 3,

    /// <summary>Paid in a bank's mobile app (แอปธนาคาร). Printable on template versions "6" and "7" only.</summary>
    MobileBanking = 4,

    /// <summary>Paid with an e-wallet (อีวอลเล็ต). Printable on template versions "6" and "7" only.</summary>
    Wallet = 5,
}
