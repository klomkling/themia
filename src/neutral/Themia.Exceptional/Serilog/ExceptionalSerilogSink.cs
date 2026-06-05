using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;

namespace Themia.Exceptional.Serilog;

/// <summary>
/// Serilog sink that persists error-level events carrying an exception into the <see cref="IExceptionStore"/>.
/// Failures are isolated to <see cref="SelfLog"/> — a store outage never breaks the application.
/// </summary>
/// <remarks>
/// <see cref="Emit"/> writes synchronously (one or two DB round-trips per error (a rollup UPDATE, plus an INSERT for first occurrences)). High-throughput hosts should
/// wrap this sink with <c>Serilog.Sinks.Async</c> to avoid blocking the logging path under error storms.
/// </remarks>
public sealed class ExceptionalSerilogSink : ILogEventSink
{
    private readonly IExceptionStore store;
    private readonly ExceptionalOptions options;

    /// <summary>Creates the sink.</summary>
    public ExceptionalSerilogSink(IExceptionStore store, ExceptionalOptions options)
    {
        this.store = store;
        this.options = options;
    }

    /// <inheritdoc />
    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Exception is null || logEvent.Level < LogEventLevel.Error)
            return;

        try
        {
            var entry = ExceptionEntryFactory.FromException(logEvent.Exception, options.ApplicationName);
            ApplyContext(entry, logEvent);
            store.LogAsync(entry).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // A logging sink is a terminal consumer with no awaiting caller, so it must never propagate —
            // including OperationCanceledException (e.g. during host shutdown), which would otherwise
            // destabilize the logging pipeline. All failures are isolated to SelfLog.
            SelfLog.WriteLine("ExceptionalSerilogSink failed to store an exception: {0}", ex);
        }
    }

    private static void ApplyContext(ExceptionEntry entry, LogEvent logEvent)
    {
        entry.Url = Read(logEvent, "Url");
        entry.HttpMethod = Read(logEvent, "HttpMethod");
        entry.Host = Read(logEvent, "Host");
        entry.IpAddress = Read(logEvent, "IpAddress");
        entry.TenantId = Read(logEvent, "TenantId");
        entry.RequestBody = Read(logEvent, "RequestBody");
        if (logEvent.Properties.TryGetValue("StatusCode", out var sc) && sc is ScalarValue { Value: int code })
            entry.StatusCode = code;
    }

    private static string? Read(LogEvent logEvent, string name)
        => logEvent.Properties.TryGetValue(name, out var v) && v is ScalarValue { Value: { } value }
            ? value.ToString()
            : null;
}
