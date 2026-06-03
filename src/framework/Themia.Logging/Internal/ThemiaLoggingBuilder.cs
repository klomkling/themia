using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;

namespace Themia.Logging;

/// <summary>
/// Internal implementation of the Themia logging builder.
/// </summary>
internal sealed class ThemiaLoggingBuilder : IThemiaLoggingBuilder
{
    private readonly List<ISerilogSinkRegistration> _registrations = new();
    private readonly List<Action<LoggerConfiguration>> _configurers = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ThemiaLoggingBuilder"/> class.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration (optional).</param>
    /// <param name="options">The logging options.</param>
    public ThemiaLoggingBuilder(
        IServiceCollection services,
        IConfiguration? configuration,
        LoggingOptions options)
    {
        Services = services;
        Configuration = configuration;
        Options = options;
    }

    /// <inheritdoc />
    public IServiceCollection Services { get; }

    /// <inheritdoc />
    public IConfiguration? Configuration { get; }

    /// <inheritdoc />
    public LoggingOptions Options { get; }

    /// <inheritdoc />
    public IThemiaLoggingBuilder AddSink(ISerilogSinkRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        _registrations.Add(registration);
        return this;
    }

    /// <inheritdoc />
    public IThemiaLoggingBuilder Configure(Action<LoggerConfiguration> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _configurers.Add(configure);
        return this;
    }

    /// <inheritdoc />
    public Serilog.ILogger BuildLogger(IServiceProvider? serviceProvider = null)
    {
        var cfg = new LoggerConfiguration();

        // Set minimum level
        cfg.MinimumLevel.Is(ParseLogLevel(Options.MinimumLevel));

        // Add enrichers
        cfg.Enrich.FromLogContext();

        if (Options.Enrichers.WithThreadId)
            cfg.Enrich.WithThreadId();

        if (Options.Enrichers.WithMachineName)
            cfg.Enrich.WithMachineName();

        if (Options.Enrichers.WithEnvironmentUserName)
            cfg.Enrich.WithEnvironmentUserName();

        // Register all sinks
        foreach (var registration in _registrations)
        {
            registration.Register(cfg, Options, serviceProvider);
        }

        // Apply custom configurations
        foreach (var configurer in _configurers)
        {
            configurer(cfg);
        }

        // Create and set global logger
        var logger = cfg.CreateLogger();
        Log.Logger = logger;

        return logger;
    }

    /// <summary>
    /// Parses a log level string to a Serilog LogEventLevel.
    /// </summary>
    /// <param name="level">The log level string.</param>
    /// <returns>The parsed LogEventLevel, or Information if parsing fails.</returns>
    private static LogEventLevel ParseLogLevel(string level)
    {
        return Enum.TryParse<LogEventLevel>(level, ignoreCase: true, out var result)
            ? result
            : LogEventLevel.Information;
    }
}
