using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Themia.Logging;

/// <summary>
/// A builder for configuring Themia logging.
/// </summary>
public interface IThemiaLoggingBuilder
{
    /// <summary>
    /// Gets the service collection.
    /// </summary>
    IServiceCollection Services { get; }

    /// <summary>
    /// Gets the configuration (if provided).
    /// </summary>
    IConfiguration? Configuration { get; }

    /// <summary>
    /// Gets the logging options.
    /// </summary>
    LoggingOptions Options { get; }

    /// <summary>
    /// Adds a custom sink registration.
    /// </summary>
    /// <param name="registration">The sink registration to add.</param>
    /// <returns>The builder for chaining.</returns>
    IThemiaLoggingBuilder AddSink(ISerilogSinkRegistration registration);

    /// <summary>
    /// Adds custom configuration to the logger.
    /// </summary>
    /// <param name="configure">The configuration action.</param>
    /// <returns>The builder for chaining.</returns>
    IThemiaLoggingBuilder Configure(Action<LoggerConfiguration> configure);

    /// <summary>
    /// Builds and initializes the logger.
    /// </summary>
    /// <param name="serviceProvider">Optional service provider for resolving dependencies.</param>
    /// <returns>The configured Serilog logger.</returns>
    Serilog.ILogger BuildLogger(IServiceProvider? serviceProvider = null);
}
