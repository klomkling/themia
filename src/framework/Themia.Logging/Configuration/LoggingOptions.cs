namespace Themia.Logging;

/// <summary>
/// Configuration options for Themia logging.
/// </summary>
public sealed class LoggingOptions
{
    /// <summary>
    /// Gets or sets the minimum log level.
    /// </summary>
    public string MinimumLevel { get; set; } = "Information";

    /// <summary>
    /// Gets or sets the console sink options.
    /// </summary>
    public ConsoleOptions Console { get; set; } = new();

    /// <summary>
    /// Gets or sets the file sink options.
    /// </summary>
    public FileOptions File { get; set; } = new();

    /// <summary>
    /// Gets or sets the enricher options.
    /// </summary>
    public EnrichersOptions Enrichers { get; set; } = new();

    /// <summary>
    /// Configuration options for the console sink.
    /// </summary>
    public sealed class ConsoleOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether console logging is enabled.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets the output template for console logs.
        /// </summary>
        public string OutputTemplate { get; set; } =
            "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}";

        /// <summary>
        /// Gets or sets a value indicating whether to use ANSI console colors.
        /// </summary>
        public bool UseAnsiConsole { get; set; } = true;
    }

    /// <summary>
    /// Configuration options for the file sink.
    /// </summary>
    public sealed class FileOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether file logging is enabled.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets the file path pattern for log files.
        /// </summary>
        public string Path { get; set; } = "logs/themia-.json";

        /// <summary>
        /// Gets or sets the rolling interval for log files (Day, Hour, Month, Infinite).
        /// </summary>
        public string RollingInterval { get; set; } = "Day";

        /// <summary>
        /// Gets or sets the maximum number of log files to retain.
        /// </summary>
        public int? RetainedFileCountLimit { get; set; } = 31;

        /// <summary>
        /// Gets or sets the maximum file size in bytes before rolling.
        /// </summary>
        public long? FileSizeLimitBytes { get; set; } = 10_000_000;

        /// <summary>
        /// Gets or sets a value indicating whether file writes should be buffered.
        /// </summary>
        public bool Buffered { get; set; } = false;

        /// <summary>
        /// Gets or sets a value indicating whether the log file should be shared with other processes.
        /// </summary>
        public bool Shared { get; set; } = false;
    }

    /// <summary>
    /// Configuration options for log enrichers.
    /// </summary>
    public sealed class EnrichersOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether to enrich logs with thread ID.
        /// </summary>
        public bool WithThreadId { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether to enrich logs with machine name.
        /// </summary>
        public bool WithMachineName { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether to enrich logs with environment user name.
        /// </summary>
        public bool WithEnvironmentUserName { get; set; } = false;
    }
}
