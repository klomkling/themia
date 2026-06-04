namespace Themia.Services.Abstractions;

/// <summary>
/// Marker interface for domain services that encapsulate business logic.
/// </summary>
/// <remarks>
/// <para><strong>Domain Services</strong> contain business logic that:</para>
/// <list type="bullet">
///   <item>Doesn't naturally fit within a single entity or value object</item>
///   <item>Operates on multiple aggregates or entities</item>
///   <item>Coordinates complex business operations</item>
///   <item>Enforces business rules and invariants</item>
/// </list>
///
/// <para><strong>Examples:</strong></para>
/// <list type="bullet">
///   <item>Pricing calculation services</item>
///   <item>Order processing services</item>
///   <item>Inventory allocation services</item>
///   <item>Payment processing logic</item>
/// </list>
///
/// <para><strong>Characteristics:</strong></para>
/// <list type="bullet">
///   <item>Stateless - no instance state</item>
///   <item>Pure business logic - no infrastructure concerns</item>
///   <item>Testable without external dependencies</item>
///   <item>Should not directly access infrastructure (repositories, etc.)</item>
/// </list>
///
/// <para><strong>Lifecycle:</strong> Typically registered as <c>Scoped</c> in DI container.</para>
/// </remarks>
/// <example>
/// <code>
/// public interface IOrderPricingService : IDomainService
/// {
///     Task&lt;decimal&gt; CalculateTotalAsync(Order order, CancellationToken ct);
///     Task&lt;decimal&gt; ApplyDiscountsAsync(Order order, List&lt;Discount&gt; discounts, CancellationToken ct);
/// }
///
/// public class OrderPricingService : IOrderPricingService
/// {
///     public async Task&lt;decimal&gt; CalculateTotalAsync(Order order, CancellationToken ct)
///     {
///         decimal subtotal = order.Items.Sum(i => i.Quantity * i.UnitPrice);
///         decimal tax = subtotal * 0.1m; // Business rule: 10% tax
///         return subtotal + tax;
///     }
/// }
/// </code>
/// </example>
public interface IDomainService : IService
{
}
