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
