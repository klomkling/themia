namespace Themia.Services.Abstractions;

/// <summary>
/// Marker interface for integration services that communicate with external systems.
/// </summary>
/// <remarks>
/// <para><strong>Integration Services</strong> handle communication with external systems:</para>
/// <list type="bullet">
///   <item>Third-party APIs (payment gateways, shipping providers, etc.)</item>
///   <item>Legacy systems integration</item>
///   <item>External microservices</item>
///   <item>Partner systems</item>
///   <item>SaaS platforms</item>
/// </list>
///
/// <para><strong>Examples:</strong></para>
/// <list type="bullet">
///   <item>Payment gateway services (Stripe, PayPal)</item>
///   <item>Shipping provider services (FedEx, UPS)</item>
///   <item>CRM integration (Salesforce, HubSpot)</item>
///   <item>ERP integration (SAP, Oracle)</item>
/// </list>
///
/// <para><strong>Characteristics:</strong></para>
/// <list type="bullet">
///   <item>Abstract external API details from domain layer</item>
///   <item>Handle authentication and authorization for external systems</item>
///   <item>Implement retry logic and circuit breakers</item>
///   <item>Transform external data models to internal models</item>
///   <item>Handle rate limiting and throttling</item>
/// </list>
///
/// <para><strong>Lifecycle:</strong> Typically registered as <c>Scoped</c> in DI container.</para>
/// </remarks>
/// <example>
/// <code>
/// public interface IPaymentGatewayService : IIntegrationService
/// {
///     Task&lt;PaymentResult&gt; ProcessPaymentAsync(PaymentRequest request, CancellationToken ct);
///     Task&lt;RefundResult&gt; RefundPaymentAsync(string transactionId, decimal amount, CancellationToken ct);
///     Task&lt;PaymentStatus&gt; GetPaymentStatusAsync(string transactionId, CancellationToken ct);
/// }
///
/// public interface IShippingService : IIntegrationService
/// {
///     Task&lt;ShippingQuote&gt; GetQuoteAsync(Address from, Address to, PackageDimensions dimensions, CancellationToken ct);
///     Task&lt;ShippingLabel&gt; CreateShipmentAsync(ShipmentRequest request, CancellationToken ct);
///     Task&lt;TrackingInfo&gt; TrackShipmentAsync(string trackingNumber, CancellationToken ct);
/// }
///
/// // Implementation with resilience
/// public class StripePaymentService : IPaymentGatewayService
/// {
///     private readonly IHttpClientFactory _httpClientFactory;
///     private readonly ILogger&lt;StripePaymentService&gt; _logger;
///
///     public async Task&lt;PaymentResult&gt; ProcessPaymentAsync(PaymentRequest request, CancellationToken ct)
///     {
///         var client = _httpClientFactory.CreateClient("Stripe");
///
///         try
///         {
///             var response = await client.PostAsJsonAsync("charges", request, ct);
///             response.EnsureSuccessStatusCode();
///
///             var result = await response.Content.ReadFromJsonAsync&lt;PaymentResult&gt;(ct);
///             return result!;
///         }
///         catch (HttpRequestException ex)
///         {
///             _logger.LogError(ex, "Failed to process payment with Stripe");
///             throw new PaymentGatewayException("Payment processing failed", ex);
///         }
///     }
/// }
/// </code>
/// </example>
public interface IIntegrationService : IService
{
}
