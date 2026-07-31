using Themia.Messaging.Outbox;

namespace Themia.Messaging.Http;

/// <summary>Maps an HTTP status onto a delivery outcome.</summary>
public static class HttpStatusClassifier
{
    /// <summary>Classifies a response status.</summary>
    /// <remarks>
    /// 408 is transient because it is the scheme's stale-timestamp status: a clock problem is
    /// infrastructure, self-heals when the clock corrects, and must retry. 401 is permanent because
    /// retrying an identical signature fails identically.
    /// </remarks>
    /// <param name="status">The HTTP status code.</param>
    /// <returns>The outcome the drainer should record.</returns>
    public static DispatchOutcome Classify(int status) => status switch
    {
        >= 200 and < 300 => DispatchOutcome.Delivered,
        408 or 425 or 429 => DispatchOutcome.Transient,
        >= 500 => DispatchOutcome.Transient,
        _ => DispatchOutcome.Permanent,
    };
}
