namespace Themia.Messaging.Outbox;

/// <summary>How a delivery attempt ended, which decides whether the row retries or dead-letters.</summary>
public enum DispatchOutcome
{
    /// <summary>Delivered; the row is completed.</summary>
    Delivered = 0,

    /// <summary>Failed for a reason a later attempt might survive; the row retries with backoff.</summary>
    Transient = 1,

    /// <summary>Failed for a reason retrying cannot fix; the row dead-letters immediately.</summary>
    Permanent = 2,
}

/// <summary>The outcome of one delivery attempt, plus the error recorded on the row when it failed.</summary>
/// <param name="Outcome">Whether the attempt succeeded, may be retried, or is permanently undeliverable.</param>
/// <param name="Error">The failure message; <see langword="null"/> when delivered.</param>
public readonly record struct DispatchResult(DispatchOutcome Outcome, string? Error)
{
    /// <summary>A successful delivery.</summary>
    /// <returns>A <see cref="DispatchOutcome.Delivered"/> result.</returns>
    public static DispatchResult Delivered() => new(DispatchOutcome.Delivered, null);

    /// <summary>A failure a later attempt might survive.</summary>
    /// <param name="error">The failure message to record on the row.</param>
    /// <returns>A <see cref="DispatchOutcome.Transient"/> result.</returns>
    public static DispatchResult Transient(string error) => new(DispatchOutcome.Transient, error);

    /// <summary>A failure retrying cannot fix.</summary>
    /// <param name="error">The failure message to record on the row.</param>
    /// <returns>A <see cref="DispatchOutcome.Permanent"/> result.</returns>
    public static DispatchResult Permanent(string error) => new(DispatchOutcome.Permanent, error);
}
