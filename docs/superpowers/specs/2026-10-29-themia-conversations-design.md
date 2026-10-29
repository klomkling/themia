# Themia.Conversations: per-object conversations

**Status:** draft for review (design only, no code). **Tracks:** coord #0182. **Target:** post-launch, no version promised.
**Basis:** written from the maintainer brief, not from the coord thread text. The decisions on the former open questions (2026-10-29) are folded into the sections and listed in §10.

## 1. Scope

A shared chat attached to one business object (a booking, a ticket, an asset) for members and for guests without an account.
Follows the repo pattern: neutral core `Themia.Conversations` (`net8.0;net10.0`, Dapper + FluentMigrator, no ASP.NET and no `Themia.Messaging` dependency), engines
`.PostgreSql` / `.MySql` / `.SqlServer`, and `.AspNetCore` for the guest-link endpoints and DI. **Not in scope:** UI, a
realtime transport (v1 is polling on open plus the batched notification of §5; SignalR comes later) and the business object itself. The object is an
opaque `(SubjectType, SubjectId)` pair, so no app domain enters Themia.

## 2. Entities (all rows carry `TenantId`; every call takes the tenant explicitly, as `Themia.Audit` does (the `.AspNetCore` adapter supplies it from the request), and every query carries the predicate; child tables reference their parent with composite `(Id, TenantId)` foreign keys from the first migration, so a row cannot point across tenants)

- **Conversation:** `Id`, `SubjectType`, `SubjectId`, `State` (`Open | Closed`), `RetentionState` (`Active | LegalHold | Expired`),
  `CreatedAt`, `ClosedAt`, `RetentionUntil`. Unique on `(TenantId, SubjectType, SubjectId)`.
- **Participant:** `Id`, `ConversationId`, `Kind` (`Member | Guest | System`), `UserRef` (opaque id, members only), `DisplayName`,
  `ContactHash` (guests; HMAC with a server-side pepper, because phone numbers are enumerable), `RoleLabel`, `JoinedAt`, `LeftAt`, `LastReadSeq`.
- **Message:** `Id`, `ConversationId`, `Seq` (dense per conversation, the only ordering), `ParticipantId`, `Kind` (`Text | System`),
  `Body` (plain text, length-capped, never HTML), `ClientMessageId` (idempotency), `CreatedAt`, `RedactedAt`. Messages change only through
  the retention component: erasure redacts, purge deletes (§6). `Seq` is allocated by `UPDATE conversation SET last_seq = last_seq + 1` inside the post
  transaction (a row lock; syntax per engine), and `(TenantId, ConversationId, ParticipantId, ClientMessageId)` is unique.
- **Attachment:** `MessageId`, `OwnerParticipantId`, `StorageKey`, `ContentType`, `Size`, `Sha256`. The key is scoped
  `{tenant}/{conversation}/{attachmentId}`; the hash is a column for integrity, not the key, so there is no cross-message or
  cross-tenant sharing and a purge or erasure never deletes bytes another row uses. Only images are re-encoded (through
  `IImageProcessor`, dropping EXIF and GPS), and their `ContentType` and `Size` come from the processor output, never from the client.
  Allow-listed non-images (PDF) are stored as-is and served download-only under the sandbox headers of §8 until the virus-scan row.

## 3. Guests and magic links

A guest is a `Participant` of `Kind.Guest`, invited over the channel they signed up with (e-mail, SMS or LINE); the app supplies the sender, the core only issues the link. The link carries one opaque token issued by
`IChallengeService` (purpose `conversation-guest`, scope = tenant + participant), so TTL, single use and the issue-side rate limits come from `Themia.Challenges`. Its token path has no attempt cap (the
bound is token entropy, so the token is at least 128 bits), so the `.AspNetCore` exchange endpoint rate-limits by address and tenant itself;
the tenant comes from the request and is passed to `VerifyByTokenAsync` explicitly. The token is **never redeemed by a GET**, because mail and LINE link previewers would burn it: the page the link opens
posts it to the exchange endpoint, the token is moved out of the URL (fragment, then stripped), and the responses are
`Cache-Control: no-store` with `Referrer-Policy: no-referrer`. The exchange yields a **guest session**: an opaque lookup key
(default 12 h) bound to one `(ConversationId, ParticipantId)`, carried in an `HttpOnly`, `Secure`, `SameSite` cookie scoped to the
conversation path. It can read and post in that conversation and nothing else. It is **not self-contained**: every request
re-reads the participant's `LeftAt` and the conversation state, so setting `LeftAt` or closing revokes at once. Redeeming consumes the token, so a guest returning after the 12 h session has no
way back except a **re-invite**: a member, or the guest asking from the expired page, triggers it (limited to 3 per hour per contact and
conversation); a fresh invite revokes the participant's earlier sessions. Tokens and sessions are never logged; an unknown, expired or revoked token gives one
indistinguishable refusal. Guests never become identity users.

## 4. Masking

A read-time projection, never a write-time edit, and best effort rather than a control (regexes miss things), so the stored text stays whole for export and DSR. `IConversationMasker`
receives the viewer (`Member | Guest`) and the message and returns what that viewer may see: guests see role labels instead of
member names, and phone numbers, e-mail addresses and chat handles in text are replaced (a default regex masker, replaceable).
Notifications and the masked export use the same projection, and notifications carry **no message body by default** ("new message" and a link); an app opts in.

## 5. Notification hook

The core sends nothing itself and cannot enlist in the `Themia.Messaging` outbox, whose only store is the net10 framework module. It owns a
transaction-aware port instead: `IConversationEventSink.RaiseAsync(DbConnection, DbTransaction, ConversationEvent, ct)`, called inside the
post transaction with `MessagePosted(ConversationId, MessageId, Seq, RecipientParticipantIds)`. The default sink writes an event row in
that transaction (a rolled-back post leaves none), and a drain job hands rows to app subscribers at least once. A later
`Themia.Modules.Conversations` (net10) may bridge the port to the Messaging outbox. The app subscribes and sends through
`Themia.Notifications`; unread digests are its job on `Themia.Scheduling`, driven by `LastReadSeq` (`UnreadSince(participant)`).

## 6. Retention and data-subject requests

`RetentionUntil` is set when a conversation closes: default **180 days** after the subject closes, a setting per `SubjectType`
(**pending counsel review** of the PDPA basis; do not treat the number as decided). A purge job deletes expired
messages and attachments (storage objects first) and leaves a tombstone (id, counts, purge time) for the audit trail;
`LegalHold` blocks it. **Export for a data subject:** everything they authored or appear in, as JSON plus an attachment
manifest. **Erasure:** their message bodies are redacted (`Body` null, `RedactedAt` set), the participant is anonymised
(contact hash cleared, name replaced), and the thread keeps its shape. Erasure also deletes the person's attachments (storage objects and rows). An export holds the subject's own messages in full and other people's messages masked or omitted (counsel decides which). Both write an `Themia.Audit` record.

## 7. Dossier export

`IConversationExporter` renders the whole transcript of a subject, ordered by `Seq`, through `Themia.Pdf` or `Themia.Export`,
with an attachment list and each file's SHA-256, capped (500 messages and 50 listed attachments per part; a larger transcript is split into parts). `ExportMode` is `Masked` (default) or `Full`; `Full` needs an explicit
permission from the app's policy and always writes an audit record, as does every read by staff.

## 8. Security defaults

Authorization is the app's, through `IConversationAccessPolicy.Can(actor, conversation, action)`, **default deny**. Posts are
rate-limited per participant and idempotent on `ClientMessageId`; guest invites are rate-limited per tenant and per contact (SMS
pumping). Bodies are escaped on output. Named caps (all options): body 4,000 characters, guest `DisplayName` 80 characters without control or markup characters, 10 attachments and 10 MiB per message,
200 MiB per conversation. Attachments are never public: each download goes through the access policy and a short-lived
presigned URL, with `Content-Disposition: attachment`, `X-Content-Type-Options: nosniff` and a sandboxing CSP.

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

Outcomes are enums with computed booleans, not bool flags. `CloseAsync` is idempotent; reopening is allowed until purge and clears `RetentionUntil`;
`MarkReadAsync` is monotonic. The ids, `Actor`, `PostMessage` and the other placeholders get concrete types in the implementation plan, with the
`PublicAPI` files and the three engine projects.

## 10. Decisions and what is still open

Decided: no realtime in v1 (polling plus batched notify, SignalR later); attachments are allow-listed, images re-encoded, stored under a tenant- and conversation-scoped key with the hash kept as a
column, with the virus scan a later row; the event port of §5 is the transaction-aware design, not the outbox; default retention 180 days after the subject closes, marked pending counsel
review; one conversation per subject in v1 (the unique key of §2 stands); guest links go over the guest's own signup channel,
sent by an app-supplied sender. **Still open:** the legal basis for each retention period (counsel, not engineering), and whether
a later "several conversations per subject" needs a `Kind` column in the unique key (a migration, not a redesign).
