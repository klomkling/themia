using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Themia.Audit;
using Themia.Audit.Redaction;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Connections;
using Themia.Modules.Identity.Abstractions.Authentication;
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
    /// <see cref="TransactionalAuditRecorder"/> as <see cref="IAuditRecorder"/> (deterministically
    /// superseding the neutral registration from <c>AddThemiaAudit</c> — see <c>Replace</c> below, not
    /// registration order), <see cref="TenantAuditReader"/> as <see cref="ITenantAuditReader"/>, and
    /// <see cref="AuditLogServiceAdapter"/> as <see cref="IAuditLogService"/>.
    /// </summary>
    /// <remarks>
    /// <b>Call order does not matter, and that is asserted rather than assumed.</b> <c>AddThemiaAudit</c>
    /// registers its neutral recorder with <c>TryAddSingleton</c>, which is a no-op when an
    /// <see cref="IAuditRecorder"/> registration already exists, so it can never displace this one; and
    /// this method uses <c>Replace</c>, so it supersedes the neutral registration by identity rather than
    /// by which call happened to run last. Either order yields the transaction-aware recorder.
    /// <c>Replace</c> also keeps a repeated call idempotent — exactly one <see cref="IAuditRecorder"/>
    /// registration, never a growing list.
    /// <para>
    /// Both packages must still be registered before the provider is built: this method's registrations
    /// resolve <see cref="IAuditStore"/>, <see cref="IAuditDialect"/>, <see cref="IAuditRedactor"/> and
    /// <see cref="AuditOptions"/>, which <c>AddThemiaAudit</c> and a provider package such as
    /// <c>AddThemiaAuditPostgreSql</c> supply.
    /// </para>
    /// </remarks>
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

        // Replace, not Add/TryAdd. Add would make the winner depend on which call ran last; TryAdd would
        // lose outright when AddThemiaAudit ran first. Replace supersedes by identity, so either call
        // order ends at this recorder, and a repeated AddThemiaAuditModule stays idempotent because
        // Replace removes the prior IAuditRecorder descriptor before adding this one. When this method
        // runs first there is nothing to remove and Replace simply adds — AddThemiaAudit's TryAddSingleton
        // is then a no-op, so this registration still stands. Scoped, not singleton, because it depends on
        // the scoped IAmbientConnectionAccessor/ITenantContext.
        services.Replace(ServiceDescriptor.Scoped<IAuditRecorder>(sp => new TransactionalAuditRecorder(
            sp.GetRequiredService<AuditRecorder>(),
            sp.GetRequiredService<IAmbientConnectionAccessor>(),
            sp.GetRequiredService<ITenantContext>(),
            sp.GetRequiredService<IOptions<AuditModuleOptions>>().Value)));

        services.TryAddScoped<ITenantAuditReader>(sp => new TenantAuditReader(
            sp.GetRequiredService<IAuditStore>(),
            sp.GetRequiredService<IAuditDialect>(),
            sp.GetRequiredService<IOptions<AuditOptions>>().Value,
            sp.GetRequiredService<ITenantContext>()));

        // TryAdd: IAuditLogService is a single-owner seam (unlike IIdentityEventObserver below), so a
        // repeated AddThemiaAuditModule call must not register a second one.
        services.TryAddScoped<IAuditLogService, AuditLogServiceAdapter>();

        return services;
    }

    /// <summary>
    /// Registers <see cref="AuditingIdentityObserver"/> into the <see cref="IIdentityEventObserver"/>
    /// fan-out (design §10/§11), so every Identity authentication and user-lifecycle event is audited.
    /// </summary>
    /// <remarks>
    /// <b>A separate call from <see cref="AddThemiaAuditModule"/>.</b> A host without Identity must never
    /// be made to reference this method, and a host with Identity that does not want auth auditing should
    /// not have to opt out of something that turned itself on.
    /// <para>
    /// <b>Uses <c>AddScoped</c>, never <c>TryAdd</c>.</b> <see cref="IIdentityEventObserver"/> is resolved
    /// as <see cref="IEnumerable{T}"/> — a fan-out seam, not a single-owner one — so <c>TryAdd</c> would
    /// silently drop this observer whenever an adopter had already registered one of their own. That
    /// silent loss is the exact defect <see cref="IIdentityEventObserver"/> exists to correct for
    /// <c>IAuthenticationHooks</c> and <c>IUserLifecycleHooks</c>; repeating it here would defeat the
    /// point.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddThemiaAuditIdentityObserver(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IIdentityEventObserver, AuditingIdentityObserver>();

        return services;
    }
}
