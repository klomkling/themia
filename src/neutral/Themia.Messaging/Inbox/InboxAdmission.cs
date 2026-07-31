namespace Themia.Messaging.Inbox;

/// <summary>
/// Whether an arriving message should be processed. Both outcomes are SUCCESS from the sender's point of
/// view — a duplicate is answered 2xx so the sender stops retrying, because retrying cannot change the
/// verdict.
/// </summary>
public enum InboxAdmission
{
    /// <summary>First time seen from this origin — process it.</summary>
    Accepted = 0,

    /// <summary>This message id was already admitted from this origin — drop it.</summary>
    Duplicate = 1,
}
