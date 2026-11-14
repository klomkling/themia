namespace Themia.Exceptional.Alerts;

/// <summary>The alert-safe view of one stored exception row. Carries no stack trace, request data or personal data.</summary>
public sealed class ExceptionAlertCandidate
{
    /// <summary><c>Exceptions.Id</c> of the row.</summary>
    public long ExceptionId { get; init; }

    /// <summary><c>Exceptions.Guid</c>; used to build the admin link.</summary>
    public Guid Guid { get; init; }

    /// <summary>Application name the row was stored under.</summary>
    public string ApplicationName { get; init; } = string.Empty;

    /// <summary>Error hash that groups repeats of the same exception.</summary>
    public string ErrorHash { get; init; } = string.Empty;

    /// <summary>The trigger that made the row a candidate.</summary>
    public ExceptionAlertTrigger Trigger { get; init; }

    /// <summary>Exception type name.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Always empty: the store no longer reads the exception message and the digest never carries it.</summary>
    [Obsolete("Always empty: alerts no longer carry the exception message. Will be removed in the next MINOR release.")]
    public string Message { get; init; } = string.Empty;

    /// <summary>How many times the error was rolled up into this row.</summary>
    public int DuplicateCount { get; init; }

    /// <summary>First seen (UTC), the row's creation date.</summary>
    public DateTime FirstSeenUtc { get; init; }

    /// <summary>Last seen (UTC).</summary>
    public DateTime LastSeenUtc { get; init; }
}
