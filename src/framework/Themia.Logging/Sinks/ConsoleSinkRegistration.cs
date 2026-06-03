using Serilog;
using Serilog.Sinks.SystemConsole.Themes;

namespace Themia.Logging;

/// <summary>
/// Registers the console sink for Serilog.
/// </summary>
public sealed class ConsoleSinkRegistration : ISerilogSinkRegistration
{
    /// <inheritdoc />
    public void Register(LoggerConfiguration loggerConfiguration, LoggingOptions options, IServiceProvider? services)
    {
        ArgumentNullException.ThrowIfNull(loggerConfiguration);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Console.Enabled)
            return;

        if (options.Console.UseAnsiConsole)
        {
            loggerConfiguration.WriteTo.Console(
                outputTemplate: options.Console.OutputTemplate,
                theme: AnsiConsoleTheme.Code);
        }
        else
        {
            loggerConfiguration.WriteTo.Console(
                outputTemplate: options.Console.OutputTemplate);
        }
    }
}
