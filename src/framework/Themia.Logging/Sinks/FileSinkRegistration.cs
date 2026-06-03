using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;

namespace Themia.Logging;

/// <summary>
/// Registers the file sink for Serilog with JSON formatting.
/// </summary>
public sealed class FileSinkRegistration : ISerilogSinkRegistration
{
    /// <inheritdoc />
    public void Register(LoggerConfiguration loggerConfiguration, LoggingOptions options, IServiceProvider? services)
    {
        ArgumentNullException.ThrowIfNull(loggerConfiguration);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.File.Enabled)
            return;

        var interval = ParseRollingInterval(options.File.RollingInterval);

        loggerConfiguration.WriteTo.File(
            formatter: new JsonFormatter(),
            path: options.File.Path,
            rollingInterval: interval,
            retainedFileCountLimit: options.File.RetainedFileCountLimit,
            fileSizeLimitBytes: options.File.FileSizeLimitBytes,
            buffered: options.File.Buffered,
            shared: options.File.Shared,
            restrictedToMinimumLevel: LogEventLevel.Verbose);
    }

    /// <summary>
    /// Parses a rolling interval string to a RollingInterval enum.
    /// </summary>
    /// <param name="interval">The interval string (e.g., "Day", "Hour").</param>
    /// <returns>The parsed RollingInterval, or Day if parsing fails.</returns>
    private static RollingInterval ParseRollingInterval(string interval)
    {
        return Enum.TryParse<RollingInterval>(interval, ignoreCase: true, out var result)
            ? result
            : RollingInterval.Day;
    }
}
