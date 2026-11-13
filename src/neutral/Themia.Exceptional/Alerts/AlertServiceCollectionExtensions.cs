using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace Themia.Exceptional.Alerts;

/// <summary>Registration of the exception alert job and its Telegram channel.</summary>
public static class AlertServiceCollectionExtensions
{
    /// <summary>
    /// Registers the exception alert job and the Telegram channel. Does nothing unless
    /// <see cref="ExceptionAlertOptions.Enabled"/> is set by <paramref name="configure"/>; when enabled the options are validated now.
    /// </summary>
    public static IServiceCollection AddThemiaExceptionalAlerts(
        this IServiceCollection services, Action<ExceptionAlertOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new ExceptionAlertOptions();
        configure(options);
        options.Validate();
        if (!options.Enabled)
            return services;

        services.TryAddSingleton(options);
        services.TryAddSingleton<IExceptionAlertStore>(sp => new DapperExceptionAlertStore(ResolveAlertDialect(sp)));

        // The bot token is part of the request URL. IHttpClientFactory logs the full URI at Information
        // by default, so every built-in logger is removed from this client. HttpClient diagnostics (the
        // System.Net.Http activity and the HttpHandlerDiagnosticListener events that OpenTelemetry's HttpClient
        // instrumentation reads) carry the URL too, and a null ActivityHeadersPropagator keeps them off this client.
        services.AddHttpClient(TelegramAlertChannel.HttpClientName)
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { ActivityHeadersPropagator = null, UseCookies = false });
        services.TryAddSingleton<IAlertChannel, TelegramAlertChannel>();
        services.AddHostedService(sp =>
        {
            var alertOptions = sp.GetRequiredService<ExceptionAlertOptions>();
            if (string.IsNullOrWhiteSpace(alertOptions.EnvironmentName))
                alertOptions.EnvironmentName = sp.GetService<IHostEnvironment>()?.EnvironmentName ?? string.Empty;
            return new ExceptionAlertService(
                sp.GetRequiredService<IExceptionAlertStore>(),
                sp.GetRequiredService<IAlertChannel>(),
                alertOptions,
                sp.GetRequiredService<ExceptionalOptions>().ApplicationName,
                sp.GetRequiredService<ILogger<ExceptionAlertService>>());
        });
        return services;
    }

    // Fails the host start with a clear message instead of a NullReference on the first run.
    private static IExceptionAlertSqlDialect ResolveAlertDialect(IServiceProvider sp) =>
        sp.GetRequiredService<IExceptionalSqlDialect>() as IExceptionAlertSqlDialect
        ?? throw new InvalidOperationException(
            "Exception alerts are enabled, but the registered IExceptionalSqlDialect does not implement " +
            nameof(IExceptionAlertSqlDialect) + ". Use the PostgreSQL, MySQL or SQL Server provider, or implement it on a custom dialect.");
}
