using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Themia.Framework.Data.EFCore.Abstractions;

/// <summary>
/// Defines a thin abstraction for configuring database providers.
/// </summary>
public interface IDatabaseProvider
{
    /// <summary>
    /// Gets the logical provider name (for example, <c>postgres</c>).
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Configures EF Core options for the target provider.
    /// </summary>
    /// <param name="optionsBuilder">Options builder to configure.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="serviceProvider">
    /// The scoped service provider for the resolving <see cref="DbContext"/>. EF Core rebuilds the
    /// options per scope when the <c>AddDbContext((serviceProvider, options) =&gt; …)</c> overload is
    /// used, so a provider can resolve request-scoped services here — e.g. an
    /// <c>ITenantAccessor</c> carrying the per-tenant connection string for DB-per-tenant routing.
    /// </param>
    void Configure(DbContextOptionsBuilder optionsBuilder, IConfiguration configuration, IServiceProvider serviceProvider);

    /// <summary>
    /// Registers provider-specific services with the dependency injection container.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    void ConfigureServices(IServiceCollection services, IConfiguration configuration);
}
