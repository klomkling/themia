using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Themia.Audit.Http;
using Themia.Audit.Redaction;

namespace Themia.Audit.DependencyInjection;

/// <summary>
/// Registers the neutral <c>Themia.Audit</c> core: options, redactor, HTTP enricher, and
/// <see cref="AuditRecorder"/>, plus (by default) the schema migration.
/// </summary>
public static class AuditServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="AuditOptions"/> (validated with <c>ValidateOnStart</c>), the redactor, the
    /// HTTP enricher, and <see cref="AuditRecorder"/> as <see cref="IAuditRecorder"/>. Call a provider
    /// package's registration (e.g. <c>AddThemiaAuditPostgreSql</c>) alongside this one — it supplies the
    /// <see cref="IAuditDialect"/> and <see cref="IAuditStore"/> the recorder registered here depends on.
    /// </summary>
    /// <remarks>
    /// Runs the FluentMigrator schema migration (unless <paramref name="runMigration"/> is
    /// <see langword="false"/>), so a consumer using <c>Themia.Audit</c> with no module layer still gets a
    /// table (design §11) — mirroring <c>Themia.Exceptional</c>'s <c>ServiceCollectionExtensions</c>. The
    /// migration itself does not run here: this call records the migration intent through
    /// <see cref="AuditMigrationHandshake"/>, and whichever of this call or the matching
    /// <c>AddThemiaAudit{Engine}</c> call completes second actually runs it (design §5.2) — the pair is
    /// order-free, so both the published call order and its reverse migrate exactly once.
    /// When <paramref name="runMigration"/> is <see langword="true"/> (the default), <see cref="AuditOptions.Engine"/>
    /// and <see cref="AuditOptions.ConnectionString"/> are checked immediately and this call throws
    /// <see cref="InvalidOperationException"/>, naming the missing setting, when either is invalid —
    /// asking for a migration and silently not getting one would surface, at best, as a "table does not
    /// exist" error at the first audit write, which is worse than failing here. Pass
    /// <paramref name="runMigration"/>: <see langword="false"/> to defer schema creation and rely on
    /// <c>ValidateOnStart</c> instead. <paramref name="configure"/> is invoked twice — once to check the
    /// options ahead of the migration, once by the options system — so it must be a pure assignment of
    /// properties, with no side effects.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Required options callback. Invoked more than once; must be pure.</param>
    /// <param name="runMigration">When <see langword="true"/> (default), applies the schema migration immediately.</param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="runMigration"/> is <see langword="true"/> and <see cref="AuditOptions.Engine"/> or
    /// <see cref="AuditOptions.ConnectionString"/> is invalid.
    /// </exception>
    public static IServiceCollection AddThemiaAudit(
        this IServiceCollection services, Action<AuditOptions> configure, bool runMigration = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<AuditOptions>()
            .Configure(configure)
            .Validate(HasValidEngine, "AuditOptions.Engine must be set to a supported, non-default AuditEngine value.")
            .Validate(HasConnectionString, "AuditOptions.ConnectionString must not be empty.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAuditRedactor>(sp =>
            new AuditRedactor(sp.GetRequiredService<IOptions<AuditOptions>>().Value.Redaction));

        services.AddHttpContextAccessor();
        services.TryAddSingleton<AuditHttpEnricher>();

        services.TryAddSingleton<IAuditRecorder>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AuditOptions>>().Value;
            return new AuditRecorder(
                sp.GetRequiredService<IAuditStore>(),
                sp.GetRequiredService<IAuditRedactor>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<IAuditDialect>(),
                options);
        });

        if (runMigration)
        {
            var probe = new AuditOptions();
            configure(probe);

            if (!HasValidEngine(probe))
            {
                throw new InvalidOperationException(
                    "AddThemiaAudit was called with runMigration: true, but AuditOptions.Engine is not "
                    + "set to a supported, non-default AuditEngine value. Set Engine in the configure "
                    + "callback, or pass runMigration: false to defer schema creation.");
            }

            if (!HasConnectionString(probe))
            {
                throw new InvalidOperationException(
                    "AddThemiaAudit was called with runMigration: true, but AuditOptions.ConnectionString "
                    + "is empty. Set ConnectionString in the configure callback, or pass "
                    + "runMigration: false to defer schema creation.");
            }

            AuditMigrationHandshake.RecordIntent(services, runMigration: true, probe.ConnectionString);
        }
        else
        {
            // Still records intent — with RunMigration: false — so the handshake has both halves to
            // consume once the matching AddThemiaAudit{Engine} call arrives, in either order, and never
            // runs the migration nobody asked for. The connection string is never read in this branch.
            AuditMigrationHandshake.RecordIntent(services, runMigration: false, connectionString: string.Empty);
        }

        return services;
    }

    private static bool HasValidEngine(AuditOptions options) =>
        Enum.IsDefined(options.Engine) && options.Engine != AuditEngine.Unspecified;

    private static bool HasConnectionString(AuditOptions options) =>
        !string.IsNullOrWhiteSpace(options.ConnectionString);
}
