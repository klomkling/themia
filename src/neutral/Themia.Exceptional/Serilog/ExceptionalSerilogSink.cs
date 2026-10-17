using Microsoft.AspNetCore.Http;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Themia.Exceptional.Middleware;

namespace Themia.Exceptional.Serilog;

/// <summary>
/// Serilog sink that persists error-level events carrying an exception into the <see cref="IExceptionStore"/>.
/// Failures are isolated to <see cref="SelfLog"/> — a store outage never breaks the application.
/// </summary>
/// <remarks>
/// <see cref="Emit"/> writes synchronously (one or two DB round-trips per error (a rollup UPDATE, plus an INSERT for first occurrences)).
/// <para>
/// Register it directly on the logger, not behind <c>Serilog.Sinks.Async</c> or another batching wrapper: it reads the
/// caller address, request body and request context from the current request while it emits, and a wrapper emits
/// on its own thread where there is no <c>HttpContext</c>, so those three would be stored empty. A host that must not
/// block on the store writes through its own bounded queue inside an <see cref="IExceptionStore"/> decorator instead.
/// </para>
/// </remarks>
public sealed class ExceptionalSerilogSink : ILogEventSink
{
    private readonly IExceptionStore store;
    private readonly ExceptionalOptions options;
    private readonly IHttpContextAccessor? accessor;

    /// <summary>Creates the sink without access to the current request: the stored entry carries only what the
    /// log event's own properties supply.</summary>
    public ExceptionalSerilogSink(IExceptionStore store, ExceptionalOptions options)
        : this(store, options, null)
    {
    }

    /// <summary>Creates the sink. With an <paramref name="accessor"/> it reads the caller address, the captured
    /// request body and (when <see cref="ExceptionalOptions.CaptureRequestContext"/> is on) the redacted request
    /// context from the current request while it stores an exception. They are never log-event properties, so
    /// no other sink sees them.</summary>
    public ExceptionalSerilogSink(IExceptionStore store, ExceptionalOptions options, IHttpContextAccessor? accessor)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        this.store = store;
        this.options = options;
        this.accessor = accessor;
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
            ApplyRequest(entry);
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
        entry.RequestContext = Read(logEvent, "RequestContext");
        if (logEvent.Properties.TryGetValue("StatusCode", out var sc) && sc is ScalarValue { Value: int code })
            entry.StatusCode = code;
    }

    // Read from the request here rather than from enriched log properties: a property would be written to every
    // sink, and these (caller IP, body, headers/cookies/form) must reach only the exception store. A value the
    // event already carries (a host's own enricher) wins.
    private void ApplyRequest(ExceptionEntry entry)
    {
        var http = accessor?.HttpContext;
        if (http is null)
            return;

        entry.IpAddress ??= http.Connection.RemoteIpAddress?.ToString();
        if (entry.RequestBody is null && http.Items.TryGetValue(RequestBodyLoggingMiddleware.BodyItemKey, out var body) && body is string bodyText)
            entry.RequestBody = bodyText;
        if (entry.RequestContext is null && options.CaptureRequestContext)
        {
            try { entry.RequestContext = RequestContextCapture.Build(http, options.Redactor); }
            catch (Exception ex)
            {
                // Logging path: never throw. A buggy custom Redactor or serialization edge must not drop
                // the error being logged; omit RequestContext only.
                SelfLog.WriteLine("Themia.Exceptional: RequestContext capture failed: {0}", ex);
            }
        }
    }

    private static string? Read(LogEvent logEvent, string name)
        => logEvent.Properties.TryGetValue(name, out var v) && v is ScalarValue { Value: { } value }
            ? value.ToString()
            : null;
}
