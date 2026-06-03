using Serilog;

namespace Themia.Logging;

/// <summary>
/// Defines a contract for registering custom Serilog sinks.
/// </summary>
public interface ISerilogSinkRegistration
{
    /// <summary>
    /// Registers a sink with the logger configuration.
    /// </summary>
    /// <param name="loggerConfiguration">The Serilog logger configuration.</param>
    /// <param name="options">The logging options.</param>
    /// <param name="services">The service provider for resolving dependencies (optional).</param>
    void Register(LoggerConfiguration loggerConfiguration, LoggingOptions options, IServiceProvider? services);
}
