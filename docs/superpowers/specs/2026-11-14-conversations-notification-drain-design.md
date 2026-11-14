# Themia.Conversations: notification drain and debounce (design, coord #0182)

Status: proposal for review. Extends §5 of `2026-10-29-themia-conversations-design.md`; nothing here changes a decision in that spec, it fixes the
shape the spec left open. Driver: opsezy PL15 (batched LINE "new message" push, launch end-January). Conversations sends nothing itself; the app
subscribes and sends through `Themia.Notifications`.

## What exists

The post transaction writes one `conversation_events` row per message (`MessagePosted`, payload: message id, `Seq`, recipient participant ids, never
text; `created_at` from the app clock). The shipped table has `delivered_at` and the indexes the debounce needs. Nothing reads the rows yet.

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

## State: a per-recipient cursor (plan review, must-fix 1)

One event row carries every recipient, but a batch is sent per (conversation, recipient), so a per-event `delivered_at` cannot record "A succeeded, B threw":
marking it loses B, not marking it re-pushes A on every drain (and a new post changes `LastSeq`, so `BatchId` dedupe no longer saves A). The state is therefore a cursor row
per recipient, in the **one forward migration** (the shipped migration is never edited; 3-engine block + replay guard like the other Themia migrations):

`conversation_notify_cursors (tenant_id, conversation_id, participant_id, notified_seq, lease_owner, lease_expires_at)`, key `(tenant_id, conversation_id, participant_id)`,
literal table name, byte-exact collation on the string columns, tenant-bound foreign key to the participant like every other child table.

Events stay as the cheap "something changed" signal (undelivered index). An event is marked `delivered_at` only when every recipient in its payload has a cursor at or past its `Seq`.

## Rules

1. **Due.** A recipient is due when it has messages from others (not redacted or deleted, the same filter as the read API's unread count) with `seq > max(last_read_seq, notified_seq)`
   and the **oldest** of those is older than `NotifyDebounce`. The window is fixed from that message and **not** reset by later posts: a busy thread notifies every window.
2. **Batch** = (conversation, recipient): `UnreadCount` and `LastSeq` computed at drain time. A recipient who left, a revoked guest and an erased person (`left_at` set) are not recipients.
3. **Every outcome moves the cursor (must-fix 4).** Success sets `notified_seq = LastSeq`. A skip (recipient read everything, left, only own messages) sets it to the recipient's current
   `last_read_seq` without calling the subscriber, so the next post starts a fresh window instead of being instantly due and rescanned forever.
4. **Failure isolation.** A subscriber exception leaves only that recipient's cursor, so A is never pushed again because B threw, and B retries on the next drain with its own `BatchId`.
   `Failed` counts them; each is logged at Warning with conversation id and participant id, never text or contact.
5. **Claim unit = the cursor row (must-fix 2), lease with an owner (must-fix 3).** Every due predicate sits inside the locked select (PostgreSQL and MySQL 8.0.1+ `FOR UPDATE SKIP LOCKED`,
   SQL Server `UPDLOCK, READPAST`, MySQL at READ COMMITTED as in `MySqlMessagingDialect`), then `UPDATE ... SET lease_owner, lease_expires_at WHERE id IN (...)`, the shape of the
   Messaging outbox claim; no outer filter on a locked subquery. A cursor row is created on first use (insert-if-absent), so a recipient with no row yet is claimable.
   The mark is `WHERE lease_owner = @me`: zero rows means the lease was lost (an expired lease was reclaimed), logged and **not** counted as `Delivered`.
   One claim takes at most `BatchSize` recipients; a drain that runs longer than the lease logs a Warning.
6. **At least once.** The drain claims and commits, calls the subscriber **outside** any transaction, then marks in a second short transaction. A crash after the push re-pushes. The re-push
   carries the same `BatchId` only if no new message arrived; otherwise it is a superseding batch with a higher `LastSeq`. `BatchId` is deterministic from (conversation, participant, `LastSeq`).
   Subscriber contract: keep a durable dedupe keyed on `BatchId`, with a TTL of at least lease + window, and do not throw for a permanent failure.
7. **Clock.** The window and the lease use the app `TimeProvider` (`@now` passed in), never the database clock, because `created_at` is app-stamped. Lease default 5 minutes;
   the README states the assumption that node clock skew is far below it. Tests use `FakeTimeProvider`, no sleeps.
8. **Retention and erasure.** The purge deletes the new cursor table with the conversation's other rows (add it to the purge order and the PostgreSQL table registration), so an undelivered batch
   is dropped, never sent after its data is gone.
9. **Validation and docs.** `BatchSize` outside 1..1000 throws `ArgumentOutOfRangeException`. An app that replaces `IConversationEventSink` writes no events, so the drain finds nothing: say so in the README.
   No subscriber registered: `SubscriberMissing`, no cursor or event touched.

## Tests (all three engines where the dialect differs)

- 20 posts in one window become one batch per recipient with the right `UnreadCount`/`LastSeq`; inside the window nothing is sent; redacted messages are not counted;
- B throws, A succeeds: A is not pushed again on the next drain even after a new post; B succeeds later with its own `BatchId`;
- recipient reads everything, then a new post: nothing is sent until the window after the new post; a left participant, a revoked guest and an erased person get nothing; a guest gets one;
- A claims and blocks inside the subscriber, a new post arrives, B drains: nothing for that recipient; A finishes and marks;
- A's lease expires, B reclaims, A's mark updates 0 rows and A's result does not count it;
- two concurrent drains deliver each recipient once; no subscriber: `SubscriberMissing`, rows untouched; a purge between claim and mark leaves no cursor row;
- `BatchSize` 0 and 1001 throw; the notification and the drain log carry no text or contact (`NoPiiInLogs` style).

## Delivery order

1. migration + cursor table + dialect claim/mark (3 engines) + tests; 2. `DrainAsync`, options, PublicAPI, README; 3. (separate PR) attachments post API; 4. AspNetCore part only if cheap.
Additive, a PATCH under the pre-1.0 policy.

## Decided

Fixed window (lead, 11-14: simpler, bounded delay; a trailing debounce never notifies a busy thread). Lease and migration (lead + plan review: a duplicate LINE push costs money, a deploy overlap makes two drainers likely).
Plan review: lead-plan-reviewer 2026-11-14, GO WITH CHANGES; the four must-fixes are rules 3 to 5 and the cursor above.
