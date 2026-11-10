namespace Themia.Exceptional.Alerts;

/// <summary>Result of one attempt to deliver a digest.</summary>
public enum AlertSendOutcome
{
    /// <summary>Delivered.</summary>
    Sent = 1,

    /// <summary>Not delivered because of a transient condition (server error, timeout, rate limit); worth retrying later.</summary>
    Transient = 2,

    /// <summary>Not delivered and retrying will not help (bad token, unknown chat, malformed request).</summary>
    Permanent = 3,
}

/// <summary>Delivers an alert digest to an operator-facing channel. Telegram is the only built-in implementation.</summary>
public interface IAlertChannel
{
    /// <summary>Sends <paramref name="digest"/>. Must not throw for delivery problems; report them as an outcome.</summary>
    Task<AlertSendOutcome> SendAsync(AlertDigest digest, CancellationToken cancellationToken);
}
