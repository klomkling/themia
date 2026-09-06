using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Themia.Audit.Http;
using Themia.Audit.Migrations;
using Themia.Audit.Redaction;
using Themia.Data.Migrations;

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
    /// Runs the FluentMigrator schema migration itself (unless <paramref name="runMigration"/> is
    /// <see langword="false"/>), so a consumer using <c>Themia.Audit</c> with no module layer still gets a
    /// table (design §11) — mirroring <c>Themia.Exceptional</c>'s <c>ServiceCollectionExtensions</c>. The
    /// migration runs only when <see cref="AuditOptions.Engine"/> and
    /// <see cref="AuditOptions.ConnectionString"/> are already valid; an invalid configuration is left to
    /// surface through the standard <see cref="IOptions{TOptions}"/> validation the next time
    /// <c>IOptions&lt;AuditOptions&gt;.Value</c> is read, rather than failing this call with a different
    /// exception shape.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Required options callback.</param>
    /// <param name="runMigration">When <see langword="true"/> (default), applies the schema migration immediately.</param>
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
            if (HasValidEngine(probe) && HasConnectionString(probe))
            {
                ThemiaMigrations.Run(ToMigrationEngine(probe.Engine), probe.ConnectionString, typeof(AuditSchemaMigration).Assembly);
            }
        }

        return services;
    }

    private static bool HasValidEngine(AuditOptions options) =>
        Enum.IsDefined(options.Engine) && options.Engine != AuditEngine.Unspecified;

    private static bool HasConnectionString(AuditOptions options) =>
        !string.IsNullOrWhiteSpace(options.ConnectionString);

    private static MigrationEngine ToMigrationEngine(AuditEngine engine) => engine switch
    {
        AuditEngine.Postgres => MigrationEngine.Postgres,
        AuditEngine.SqlServer => MigrationEngine.SqlServer,
        AuditEngine.MySql => MigrationEngine.MySql,
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown AuditEngine value."),
    };
}
