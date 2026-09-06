using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Themia.Audit;
using Themia.Audit.Redaction;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Connections;
using Themia.Services.Abstractions;

namespace Themia.Modules.Audit.DependencyInjection;

/// <summary>
/// Registers the framework-side audit module: the transaction-aware recorder, the tenant-scoped reader,
/// and the <see cref="IAuditLogService"/> adapter over the neutral <c>Themia.Audit</c> core.
/// </summary>
public static class AuditModuleServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="AuditModuleOptions"/> (validated with <c>ValidateOnStart</c>),
    /// <see cref="TransactionalAuditRecorder"/> as <see cref="IAuditRecorder"/> (superseding the neutral
    /// registration from <c>AddThemiaAudit</c>), <see cref="TenantAuditReader"/> as
    /// <see cref="ITenantAuditReader"/>, and <see cref="AuditLogServiceAdapter"/> as
    /// <see cref="IAuditLogService"/>. Call <c>AddThemiaAudit</c> (and a provider package such as
    /// <c>AddThemiaAuditPostgreSql</c>) first — this method depends on the <see cref="IAuditStore"/>,
    /// <see cref="IAuditDialect"/>, <see cref="IAuditRedactor"/>, and <see cref="AuditOptions"/> those
    /// register.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional callback to tune <see cref="AuditModuleOptions"/>.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddThemiaAuditModule(
        this IServiceCollection services, Action<AuditModuleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var optionsBuilder = services.AddOptions<AuditModuleOptions>();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        optionsBuilder
            .Validate(
                o => Enum.IsDefined(o.ActivityPolicy) && o.ActivityPolicy != AuditTransactionPolicy.Unspecified,
                "AuditModuleOptions.ActivityPolicy must be a defined, non-default AuditTransactionPolicy value.")
            .ValidateOnStart();

        // Background work and hosts with no tenant infrastructure still need a resolvable ITenantContext;
        // TryAdd lets a host's own (e.g. AspNetCore per-request) context win.
        services.TryAddScoped<ITenantContext, AmbientTenantContext>();

        // A second AuditRecorder instance, distinct from the one AddThemiaAudit built for its own
        // (superseded) IAuditRecorder registration below — cheap, since that first instance is never
        // constructed once TransactionalAuditRecorder is the last IAuditRecorder registration standing.
        services.TryAddSingleton(sp => new AuditRecorder(
            sp.GetRequiredService<IAuditStore>(),
            sp.GetRequiredService<IAuditRedactor>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IAuditDialect>(),
            sp.GetRequiredService<IOptions<AuditOptions>>().Value));

        // Deliberately Add, not TryAdd: this supersedes AddThemiaAudit's IAuditRecorder registration so
        // every consumer resolving IAuditRecorder gets the transaction-aware recorder (design §7).
        // Scoped, not singleton, because it depends on the scoped IAmbientConnectionAccessor/ITenantContext.
        services.AddScoped<IAuditRecorder>(sp => new TransactionalAuditRecorder(
            sp.GetRequiredService<AuditRecorder>(),
            sp.GetRequiredService<IAmbientConnectionAccessor>(),
            sp.GetRequiredService<ITenantContext>(),
            sp.GetRequiredService<IOptions<AuditModuleOptions>>().Value));

        services.TryAddScoped<ITenantAuditReader>(sp => new TenantAuditReader(
            sp.GetRequiredService<IAuditStore>(),
            sp.GetRequiredService<IAuditDialect>(),
            sp.GetRequiredService<IOptions<AuditOptions>>().Value,
            sp.GetRequiredService<ITenantContext>()));

        services.AddScoped<IAuditLogService, AuditLogServiceAdapter>();

        return services;
    }
}
