using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Themia.Logging;

/// <summary>
/// Extension methods for configuring Themia logging services.
/// </summary>
public static class ServiceCollectionExtensions
{
    private const string SerilogSection = "Serilog";
    private const string ThemiaSection = "Themia:Logging";

    /// <summary>
    /// Adds Themia logging with default configuration (Console and File sinks).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The logging builder for further configuration.</returns>
    public static IThemiaLoggingBuilder AddThemiaLogging(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new LoggingOptions();
        var builder = new ThemiaLoggingBuilder(services, configuration: null, options);

        builder
            .AddSink(new ConsoleSinkRegistration())
            .AddSink(new FileSinkRegistration());

        WireMicrosoftExtensionsLogging(services);
        builder.BuildLogger();

        return builder;
    }

    /// <summary>
    /// Adds Themia logging with code-based configuration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Action to configure the Serilog logger.</param>
    /// <returns>The logging builder for further configuration.</returns>
    public static IThemiaLoggingBuilder AddThemiaLogging(
        this IServiceCollection services,
        Action<LoggerConfiguration> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new LoggingOptions();
        var builder = new ThemiaLoggingBuilder(services, configuration: null, options);

        builder
            .AddSink(new ConsoleSinkRegistration())
            .AddSink(new FileSinkRegistration())
            .Configure(configure);

        WireMicrosoftExtensionsLogging(services);
        builder.BuildLogger();

        return builder;
    }

    /// <summary>
    /// Adds Themia logging with configuration from appsettings.json.
    /// Supports both standard "Serilog" section and strongly-typed "Themia:Logging" section.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration root.</param>
    /// <returns>The logging builder for further configuration.</returns>
    public static IThemiaLoggingBuilder AddThemiaLogging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new LoggingOptions();
        var builder = new ThemiaLoggingBuilder(services, configuration, options);

        // Check if Serilog section exists (standard Serilog configuration)
        var serilogSection = configuration.GetSection(SerilogSection);
        if (serilogSection.Exists())
        {
            // Use Serilog's built-in configuration reading
            var cfg = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration);

            Log.Logger = cfg.CreateLogger();
        }
        else
        {
            // Check for Themia-specific section
            var themiaSection = configuration.GetSection(ThemiaSection);
            if (themiaSection.Exists())
            {
                themiaSection.Bind(options);
            }

            builder
                .AddSink(new ConsoleSinkRegistration())
                .AddSink(new FileSinkRegistration());

            builder.BuildLogger();
        }

        WireMicrosoftExtensionsLogging(services);

        return builder;
    }

    /// <summary>
    /// Wires Serilog to Microsoft.Extensions.Logging.
    /// </summary>
    /// <param name="services">The service collection.</param>
    private static void WireMicrosoftExtensionsLogging(IServiceCollection services)
    {
        services.AddLogging(loggingBuilder =>
        {
            loggingBuilder.ClearProviders();
            loggingBuilder.AddSerilog(dispose: true);
        });

        services.AddSingleton<Serilog.ILogger>(_ => Log.Logger);
    }
}
