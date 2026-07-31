namespace Themia.Messaging.Inbox;

/// <summary>
/// Whether an arriving message should be processed. All three outcomes are SUCCESS from the sender's
/// point of view — a duplicate or a stale snapshot is answered 2xx so the sender stops retrying,
/// because retrying cannot change the verdict.
/// </summary>
public enum InboxAdmission
{
    /// <summary>First time seen and not stale — process it.</summary>
    Accepted = 0,

    /// <summary>This message id was already admitted from this origin — drop it.</summary>
    Duplicate = 1,

    /// <summary>A newer version for the same entity key has already been applied — drop it.</summary>
    Stale = 2,
}
