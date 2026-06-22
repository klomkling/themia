using System.Text.RegularExpressions;

namespace Themia.Exceptional;

/// <summary>Configuration for the Themia exception-logging engine and capture pipeline.</summary>
public sealed class ExceptionalOptions
{
    /// <summary>Logical application name stamped on every stored exception. Required.</summary>
    public string ApplicationName { get; set; } = string.Empty;

    /// <summary>Window within which a repeat of the same error is rolled up instead of inserted. Default 10 minutes.</summary>
    public TimeSpan RollupPeriod { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Enable the request-body logging middleware. Off by default (PII/secret risk).</summary>
    public bool CaptureRequestBody { get; set; }

    /// <summary>Maximum bytes of request body captured when <see cref="CaptureRequestBody"/> is enabled.</summary>
    public int MaxBodyBytes { get; set; } = 4096;

    /// <summary>
    /// Include the request query string in the captured <c>Url</c>. Off by default — query parameters
    /// commonly carry secrets (<c>?token=</c>, <c>?api_key=</c>) that would otherwise be persisted.
    /// </summary>
    public bool CaptureQueryString { get; set; }

    /// <summary>
    /// Capture request context (headers, cookies, query, form, server variables) into the stored
    /// <see cref="ExceptionEntry.RequestContext"/>. Off by default — it persists more request data, so
    /// it is opt-in (and runs through <see cref="Redactor"/>).
    /// </summary>
    public bool CaptureRequestContext { get; set; }

    /// <summary>
    /// Per key/value redaction applied to every captured request-context entry: returns the value to
    /// store, a masked value, or <see langword="null"/> to drop the entry. Defaults to
    /// <see cref="DefaultRedactor"/> (masks only categorical secrets). Set to <see langword="null"/> to
    /// capture everything verbatim (the host then owns that data-protection choice).
    /// </summary>
    public Func<string, string, string?>? Redactor { get; set; } = DefaultRedactor;

    private static readonly Regex SecretKey = new(
        "authorization|^cookie$|^set-cookie$|password|secret|token|apikey|session",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Default redactor: masks values whose key names a categorical secret
    /// (Authorization/Cookie/Set-Cookie or contains password/secret/token/apikey/session) to
    /// <c>"***"</c>; returns all other values unchanged.</summary>
    public static string? DefaultRedactor(string key, string value)
        => SecretKey.IsMatch(key) ? "***" : value;

    /// <summary>Validates the options. Throws <see cref="InvalidOperationException"/> on invalid configuration.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApplicationName))
            throw new InvalidOperationException("ExceptionalOptions.ApplicationName must be set.");
        if (RollupPeriod < TimeSpan.Zero)
            throw new InvalidOperationException("ExceptionalOptions.RollupPeriod must not be negative.");
        if (MaxBodyBytes <= 0)
            throw new InvalidOperationException("ExceptionalOptions.MaxBodyBytes must be positive.");
    }
}
