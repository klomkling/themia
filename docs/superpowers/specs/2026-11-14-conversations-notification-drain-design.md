# Themia.Conversations: notification drain and debounce (design, coord #0182)

Status: proposal for review. Extends §5 of `2026-10-29-themia-conversations-design.md`; nothing here changes a decision in that spec, it fixes the
shape the spec left open. Driver: opsezy PL15 (batched LINE "new message" push, launch end-January). Conversations sends nothing itself; the app
subscribes and sends through `Themia.Notifications`.

## What exists

The post transaction writes one `conversation_events` row per message (`MessagePosted`, payload: message id, `Seq`, recipient participant ids, never
text). The shipped table already has `delivered_at` (nullable) and the tenant + conversation index the debounce needs. Nothing reads the rows yet.

## Surface (mirrors `IConversationRetention.SweepAsync`: the app schedules the call from its own `Themia.Scheduling` job)

```csharp
public interface IConversationNotifications
{
    Task<DrainResult> DrainAsync(DrainOptions options, CancellationToken ct = default);
}
public sealed record DrainOptions(int BatchSize = 100);                       // 1..1000 batches per call
public sealed record DrainResult(DrainOutcome Outcome, int Delivered, int SkippedRead, int Failed);
public enum DrainOutcome { Completed = 0, SubscriberMissing = 1 }              // enum, not bool: a new state breaks a switch

public interface IConversationNotificationSubscriber                           // one port; the app fans out itself
{
    Task NotifyAsync(ConversationNotification notification, CancellationToken ct = default);
}
public sealed record ConversationNotification(Guid BatchId, string TenantId, Guid ConversationId, Guid ParticipantId, int UnreadCount, long LastSeq);
public sealed class ConversationOptions { public TimeSpan NotifyDebounce { get; set; } = TimeSpan.FromMinutes(2); }  // new option
```

No subscriber registered: `DrainAsync` returns `SubscriberMissing` and touches no row (as `HoldPortMissing` does), so rows are never marked delivered into the void.

## Rules

1. **Window.** A conversation is due when its oldest undelivered event is older than `NotifyDebounce`. The window is fixed from that first event and is
   **not** reset by later posts: a busy thread notifies every window, never "after it goes quiet" (a thread that never goes quiet would never notify).
2. **Batch unit** = (conversation, recipient participant). `UnreadCount` and `LastSeq` are computed at drain time from `conversation_messages` after
   the recipient's `LastReadSeq`, so a recipient who read in the meantime gets nothing (`SkippedRead`), and a batch never counts own messages.
3. **Recipients** come from the events' payload, filtered at drain time to participants still active in the conversation (left participants and revoked
   guests are dropped). The notification carries ids and counts only; the app resolves contact and LINE id, and Conversations never stores them.
4. **At least once, stable id.** `BatchId` is deterministic from (conversation, participant, `LastSeq`), so a redelivery after a crash carries the same id and the
   subscriber dedupes on it. After a successful `NotifyAsync` the events up to `LastSeq` get `delivered_at`; a subscriber exception leaves them for the next drain.
5. **No external call inside a transaction.** The drain claims, commits, calls the subscriber, then marks delivered in a second short transaction.
6. **Claim = lease.** Two drainers (two nodes) must not both send a LINE push. Claim with one atomic statement per engine (PostgreSQL `FOR UPDATE SKIP LOCKED`, MySQL 8.0.1+
   `SKIP LOCKED`, SQL Server `UPDLOCK, READPAST`, same family as the Messaging outbox dialects) setting a new `claimed_until` (default 5 minutes); an expired lease is reclaimable.
   **This needs a new forward migration** adding `claimed_until` to `conversation_events` (the shipped migration is never edited), with the 3-engine block and the replay guard
   other Themia migrations carry. The alternative without a column (no claim, rely on `BatchId` dedupe) makes duplicate pushes the normal outcome of two nodes; rejected.
7. **Poison.** There is no attempts column: a batch whose subscriber always throws is retried each drain. The subscriber must not throw for a permanent failure (log and return);
   `Failed` in the result is the alarm. Revisit (attempts + dead state) only if opsezy sees it.
8. **Retention.** Purge and erasure already delete a conversation's events with it; an undelivered batch is dropped, never sent after the data it describes is gone.
   Delivered rows are removed by the same purge; no separate trimming job in this part.

## Tests (each on all three engines where the dialect differs)

- one due window with 20 posts becomes one batch per recipient with the right `UnreadCount`/`LastSeq`; inside the window, nothing is sent;
- a recipient who read past `LastSeq` is skipped; a left participant gets nothing; a guest gets one;
- two concurrent drains deliver each batch once (lease); an expired lease is reclaimed; a subscriber that throws leaves the rows and a re-run delivers with the same `BatchId`;
- no subscriber registered: `SubscriberMissing`, rows untouched; a purge between claim and mark leaves no row delivered for a gone conversation;
- the notification and the log carry no message text (`NoPiiInLogs` style test).

## Delivery order

1. migration + dialect claim/mark (3 engines) + tests; 2. `DrainAsync` + options + PublicAPI + README; 3. (separate PR) attachments post API; 4. AspNetCore part only if cheap.
Release as a PATCH (additive, pre-1.0 policy).

## Open points for the owner

- Fixed window (rule 1) versus trailing debounce: I recommend fixed; say if opsezy wants "after quiet".
- Rule 6 adds a migration; say if a lease-less first cut is acceptable instead.
