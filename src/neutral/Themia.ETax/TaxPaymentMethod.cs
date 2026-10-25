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
}
