# Themia.Conversations: per-object conversations

**Status:** draft for review (design only, no code). **Tracks:** coord #0182. **Target:** post-launch, no version promised.
**Basis:** written from the maintainer brief, not from the coord thread text. The decisions on the former open questions (2026-10-29) are folded into the sections and listed in §10.

## 1. Scope

A shared chat attached to one business object (a booking, a ticket, an asset) for members and for guests without an account.
Follows the repo pattern: neutral core `Themia.Conversations` (Dapper + FluentMigrator, no ASP.NET dependency), engines
`.PostgreSql` / `.MySql` / `.SqlServer`, and `.AspNetCore` for the guest-link endpoints and DI. **Not in scope:** UI, a
realtime transport (v1 is polling on open plus the batched notification of §5; SignalR comes later) and the business object itself. The object is an
opaque `(SubjectType, SubjectId)` pair, so no app domain enters Themia.

## 2. Entities (all rows carry `TenantId`; every query carries the tenant predicate)

- **Conversation:** `Id`, `SubjectType`, `SubjectId`, `State` (`Open | Closed`), `RetentionState` (`Active | LegalHold | Expired`),
  `CreatedAt`, `ClosedAt`, `RetentionUntil`. Unique on `(TenantId, SubjectType, SubjectId)`.
- **Participant:** `Id`, `ConversationId`, `Kind` (`Member | Guest | System`), `UserRef` (opaque id, members only), `DisplayName`,
  `ContactHash` (guests), `RoleLabel`, `JoinedAt`, `LeftAt`, `LastReadSeq`.
- **Message:** `Id`, `ConversationId`, `Seq` (dense per conversation, the only ordering), `ParticipantId`, `Kind` (`Text | System`),
  `Body` (plain text, length-capped, never HTML), `ClientMessageId` (idempotency), `CreatedAt`, `RedactedAt`. Messages are never
  updated or deleted by the app: erasure redacts (§6).
- **Attachment:** `MessageId`, `StorageKey` (through `IStorageProvider`), `ContentType` and `Size` (both allow-listed), `Sha256`
  of the stored bytes. Images are re-encoded through `Themia.Imaging` before storing, which drops EXIF and GPS data, and the object
  is stored under its hash. A virus scan is a later row; the model leaves room for a `ScanState` enum without a migration of meaning.

## 3. Guests and magic links

A guest is a `Participant` of `Kind.Guest`, invited over the channel they signed up with (e-mail, SMS or LINE); the app supplies the sender, the core only issues the link. The link carries one opaque token issued by
`IChallengeService` (purpose `conversation-guest`, scope = tenant + participant), so TTL, single use, attempt cap and both
rate limits come from `Themia.Challenges`. Exchanging the token yields a **guest session**: a signed, short-lived (default 12 h)
credential bound to one `(ConversationId, ParticipantId)` that can read and post in that conversation and nothing else. It is
revoked by setting `LeftAt`. Tokens and sessions are never logged; an unknown, expired or revoked token gives one
indistinguishable refusal. Guests never become identity users.

## 4. Masking

A read-time projection, never a write-time edit, so the stored text stays whole for export and DSR. `IConversationMasker`
receives the viewer (`Member | Guest`) and the message and returns what that viewer may see: guests see role labels instead of
member names, and phone numbers, e-mail addresses and chat handles in text are replaced (a default regex masker, replaceable).
Notifications and the masked export use the same projection, so a channel cannot leak what the page hides.

## 5. Notification hook

The core sends nothing itself. After commit it raises `MessagePosted(ConversationId, MessageId, Seq, RecipientParticipantIds)`
through the `Themia.Messaging` outbox, so a lost process never loses the event and a rolled-back post raises none. The app
subscribes and sends through `Themia.Notifications`, using the masked body. Unread digests are the app's job on
`Themia.Scheduling`, driven by `Participant.LastReadSeq`; the core exposes `UnreadSince(participant)`.

## 6. Retention and data-subject requests

`RetentionUntil` is set when a conversation closes: default **180 days** after the subject closes, a setting per `SubjectType`
(**pending counsel review** of the PDPA basis; do not treat the number as decided). A purge job deletes expired
messages and attachments (storage objects first) and leaves a tombstone (id, counts, purge time) for the audit trail;
`LegalHold` blocks it. **Export for a data subject:** everything they authored or appear in, as JSON plus an attachment
manifest. **Erasure:** their message bodies are redacted (`Body` null, `RedactedAt` set), the participant is anonymised
(contact hash cleared, name replaced), and the thread keeps its shape. Both write an `Themia.Audit` record.

## 7. Dossier export

`IConversationExporter` renders the whole transcript of a subject, ordered by `Seq`, through `Themia.Pdf` or `Themia.Export`,
with an attachment list and each file's SHA-256. `ExportMode` is `Masked` (default) or `Full`; `Full` needs an explicit
permission from the app's policy and always writes an audit record.

## 8. Security defaults

Authorization is the app's, through `IConversationAccessPolicy.Can(actor, conversation, action)`, **default deny**. Posts are
rate-limited per participant and idempotent on `ClientMessageId`. Bodies are escaped on output. Attachments are served with
`Content-Disposition: attachment` and the allow-listed type only.

## 9. Public API sketch

```csharp
public interface IConversationService
{
    Task<Conversation> OpenAsync(ConversationSubject subject, CancellationToken ct = default);
    Task<PostResult> PostAsync(PostMessage message, CancellationToken ct = default);   // Posted | Duplicate | Closed | Refused
    Task<MessagePage> ListAsync(ConversationId id, Actor viewer, long afterSeq, int limit, CancellationToken ct = default);
    Task MarkReadAsync(ConversationId id, ParticipantId who, long seq, CancellationToken ct = default);
    Task<GuestInvite> InviteGuestAsync(ConversationId id, GuestContact contact, CancellationToken ct = default);
    Task CloseAsync(ConversationId id, CancellationToken ct = default);
}
public interface IGuestAccess { Task<GuestSessionResult> ExchangeAsync(string token, CancellationToken ct = default); }
public interface IConversationRetention { Task<DataSubjectExport> ExportAsync(ParticipantRef who, ...); Task EraseAsync(ParticipantRef who, ...); }
public interface IConversationExporter { Task<ExportedDocument> ExportAsync(ConversationSubject s, ExportMode mode, ...); }
```

Outcomes are enums with computed booleans, not bool flags.

## 10. Decisions and what is still open

Decided: no realtime in v1 (polling plus batched notify, SignalR later); attachments are allow-listed, image-re-encoded and
hash-stored, with the virus scan a later row; default retention 180 days after the subject closes, marked pending counsel
review; one conversation per subject in v1 (the unique key of §2 stands); guest links go over the guest's own signup channel,
sent by an app-supplied sender. **Still open:** the legal basis for each retention period (counsel, not engineering), and whether
a later "several conversations per subject" needs a `Kind` column in the unique key (a migration, not a redesign).
