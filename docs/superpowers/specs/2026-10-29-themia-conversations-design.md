# Themia.Conversations: per-object conversations

**Status:** draft for review (design only, no code). **Tracks:** coord #0182. **Target:** post-launch, no version promised.
**Basis:** written from the maintainer brief, not from the coord thread text. The decisions on the former open questions (2026-10-29) are folded into the sections and listed in §10.

## 1. Scope

A shared chat attached to one business object (a booking, a ticket, an asset) for members and for guests without an account.
Follows the repo pattern: neutral core `Themia.Conversations` (`net8.0;net10.0`, Dapper + FluentMigrator, no ASP.NET and no `Themia.Messaging` dependency), engines
`.PostgreSql` / `.MySql` / `.SqlServer`, and `.AspNetCore` for the guest-link endpoints and DI. **Not in scope:** UI, a
realtime transport (v1 is polling on open plus the batched notification of §5; SignalR comes later) and the business object itself. The object is an
opaque `(SubjectType, SubjectId)` pair, so no app domain enters Themia.

## 2. Entities (all rows carry `TenantId`; every call takes the tenant explicitly, as `Themia.Audit` does (the `.AspNetCore` adapter supplies it from the request), and every query carries the predicate; child tables reference their parent with composite foreign keys from the first migration that bind the tenant **and** the conversation: messages to participants on `(TenantId, ConversationId, ParticipantId)`, attachments to messages and to participants on `(TenantId, ConversationId, ...)`, each parent exposing the matching unique key, so a row can point neither across tenants nor across conversations; the compared string columns (`TenantId`, `SubjectType`, `SubjectId`, `UserRef`, `ClientMessageId`, `StorageKey`, `PepperKeyId`) use a binary collation on MySQL and SQL Server, without which `Acme` and `acme` would compare equal and the keys above would not isolate anything there)

- **Conversation:** `Id`, `SubjectType`, `SubjectId`, `State` (`Open | Closed`), `RetentionState` (`Active | LegalHold | Expired`), `LastSeq` (the counter of the `Seq` rule below),
  `CreatedAt`, `ClosedAt`, `RetentionUntil`. Unique on `(TenantId, SubjectType, SubjectId)`.
- **Participant:** `Id`, `ConversationId`, `Kind` (`Member | Guest | System`), `UserRef` (opaque id, members only; an agent of another app joining through the partner API is a `Member` whose `UserRef` is an opaque id from that app's namespace), `DisplayName`,
  `ContactHash` with `PepperKeyId` (guests; HMAC of the normalised contact with a versioned server-side pepper, because phone numbers are enumerable; lookup tries every active key id and a hit re-hashes under the current one, so rotation breaks neither the re-invite limit nor DSR lookup), `RoleLabel`, `JoinedAt`, `LeftAt`, `LastReadSeq`. A person is one participant per conversation: `UNIQUE (TenantId, ConversationId, UserRef)` and `UNIQUE (TenantId, ConversationId, ContactHash)`, filtered to non-null values on PostgreSQL and SQL Server. The lookup indexes `(TenantId, ContactHash)` and `(TenantId, UserRef)` find the same person across conversations for a data-subject request.
- **Message:** `Id`, `ConversationId`, `Seq` (dense per conversation, the only ordering), `ParticipantId`, `Kind` (`Text | System`),
  `Body` (plain text, length-capped, never HTML), `ClientMessageId` (idempotency), `CreatedAt`, `RedactedAt`. **v1 has no user edit and no user delete:** a message changes only through the retention component (erasure redacts, purge deletes, §6) or a staff
  redaction, which is audited and leaves the tombstone (`RedactedAt`); an edit history would be a second store of the same personal data (§6). `Seq` is allocated by `UPDATE conversation SET last_seq = last_seq + 1` inside the post
  transaction (a row lock; syntax per engine), and `(TenantId, ConversationId, ParticipantId, ClientMessageId)` is unique.
- **GuestSession:** `Id`, `ParticipantId`, `KeyHash` (the cookie key, hashed at rest), `CreatedAt`, `ExpiresAt`, `RevokedAt` (§3).
- **ConversationEvent:** `Id`, `ConversationId` (not null, no foreign key; indexed with the tenant, so the debounce of §5 and the purge of §6 find a conversation's events without parsing the payload), `Kind`, `Payload`, `CreatedAt`, `DeliveredAt` (§5; the event id is what a subscriber dedupes on).
- **Tombstone:** `ConversationId`, `SubjectType`, `SubjectId`, `MessageCount`, `AttachmentCount`, `PurgedAt` (§6), kept after the conversation is gone.
- **Attachment:** `ConversationId`, `MessageId`, `OwnerParticipantId`, `StorageKey` (the bytes live in `Themia.Storage` through `IStorageProvider`, the package's one attachment dependency; `Themia.Imaging` re-encodes images), `ContentType`, `Size`, `Sha256`. The key is scoped
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
re-reads the participant's `LeftAt` and the conversation state, so setting `LeftAt` or closing revokes at once. Redeeming consumes the token, so a guest returning after the 12 h session has no way back except a **re-invite**. The expired page
asks the guest to type their contact; the server matches its keyed hash against the tenant's participants and answers **identically
whether or not it matched** (same text, same timing, "if this contact is in the conversation a new link was sent"), so the page is no
oracle for who is in a conversation. On a match it issues a new token and has the app's sender deliver it to the contact just typed,
which the hash proved equal to the stored one; limited to 3 per hour per contact and conversation, and per address and tenant. The
earlier sessions are revoked **only when the new token is redeemed**, not when it is issued, so a stranger who knows a contact and a
conversation URL cannot log the real guest out. A member can also re-invite. Tokens and sessions are never logged; an unknown, expired or revoked token gives one
indistinguishable refusal. Guests never become identity users.

## 4. Masking

A read-time projection, never a write-time edit, and best effort rather than a control (regexes miss things), so the stored text stays whole for export and DSR. `IConversationMasker`
receives the viewer (`Member | Guest`) and the message and returns what that viewer may see: guests see role labels instead of
member names, and phone numbers, e-mail addresses and chat handles in text are replaced (a default regex masker, replaceable).
Notifications and the masked export use the same projection, and guests see the other participants' **display names** (names only; contact details are masked, and role labels replace names for members who are staff-only) and notifications carry **no message body by default** ("new message" and a link); an app opts in.

## 5. Notification hook

The core sends nothing itself and cannot enlist in the `Themia.Messaging` outbox, whose only store is the net10 framework module. It owns a
transaction-aware port instead: `IConversationEventSink.RaiseAsync(DbConnection, DbTransaction, ConversationEvent, ct)`, called inside the
post transaction with `MessagePosted(ConversationId, MessageId, Seq, RecipientParticipantIds)`. The default sink writes an event row in
that transaction (a rolled-back post leaves none), and a drain job hands rows to app subscribers at least once, **debounced**: within `NotifyDebounce` (default 2 minutes) the posts a recipient has not read in one conversation become one batched event (count and last `Seq`), so a busy thread sends one notification, not twenty. A later
`Themia.Modules.Conversations` (net10) may bridge the port to the Messaging outbox. The app subscribes and sends through
`Themia.Notifications`; unread digests are its job on `Themia.Scheduling`, driven by `LastReadSeq` (`UnreadSince(participant)`).

## 6. Retention and data-subject requests

`RetentionUntil` is set when a conversation closes: default **180 days** after the subject closes, a setting per `SubjectType`
(**pending counsel review** of the PDPA basis; do not treat the number as decided). A purge job deletes expired
messages and attachments (storage objects first) and leaves a tombstone (id, counts, purge time) for the audit trail;
`LegalHold` blocks it. The purge locks (or flips) the conversation row first and only then deletes the children, so it cannot deadlock with a concurrent post. **Purge deletes the conversation row too** (after writing the `Tombstone`), so the unique
subject key of §2 is free again and `OpenAsync` on that subject starts a new conversation with a new id. Purge and erasure also delete the person's or conversation's `GuestSession` and `ConversationEvent` rows (the event payloads hold participant ids and counts), so neither outlives retention. `RetentionState.Expired` is only the
marker between the retention date and the purge run: `OpenAsync` on an `Expired` conversation refuses with `PendingPurge`, and on a
`Closed` one reopens it (clearing `RetentionUntil`). **Export for a data subject:** everything they authored or appear in, as JSON plus an attachment
manifest. **Erasure:** their message bodies are redacted (`Body` null, `RedactedAt` set), the participant is anonymised
(contact hash cleared, name replaced), and the thread keeps its shape. An erasure request against a conversation under `LegalHold` returns `DeferredByHold` and is queued until the hold is lifted; otherwise it returns `Erased`. Which should win is a legal question (§10). Erasure also deletes the person's attachments (storage objects and rows). An export holds the subject's own messages in full and other people's messages masked or omitted (counsel decides which). Both write an `Themia.Audit` record.

## 7. Dossier export

`IConversationExporter` renders the whole transcript of a subject, ordered by `Seq`, through `Themia.Pdf` or `Themia.Export`,
with an attachment list and each file's SHA-256, capped (500 messages and 50 listed attachments per part; a larger transcript is split into parts). `ExportMode` is `Masked` (default) or `Full`; `Full` needs an explicit
permission from the app's policy and always writes an audit record, as does a staff read of a conversation they are not a participant in. That read is audited **once on open and on each export**, never per poll (one row per actor, conversation and session window of 15 minutes), so the audit table is not a poll log.

## 8. Security defaults

Authorization is the app's, through `IConversationAccessPolicy.Can(actor, conversation, action)`, **default deny**. `Actor` carries its scope, so the policy accepts partner-scoped actors (an agent of one app in a conversation of another, arriving through the partner API) as well as members and guests; the scope is part of every `Can` decision and of the audit row. Posts are
rate-limited per participant and idempotent on `ClientMessageId`; guest invites are rate-limited per tenant and per contact (SMS
pumping). Bodies are escaped on output. Named caps (all options): body 4,000 characters, guest `DisplayName` 80 characters without control or markup characters, 10 attachments and 10 MiB per message,
200 MiB per conversation. Attachments are never public: each download goes through the access policy and a short-lived
presigned URL, with `Content-Disposition: attachment`, `X-Content-Type-Options: nosniff` and a sandboxing CSP.

## 9. Public API sketch

```csharp
public interface IConversationService
{
    Task<OpenResult> OpenAsync(Actor actor, ConversationSubject subject, CancellationToken ct = default);   // Opened | Reopened | PendingPurge
    Task<PostResult> PostAsync(Actor actor, PostMessage message, CancellationToken ct = default);   // Posted | Duplicate | Closed | Refused
    Task<MessagePage> ListAsync(ConversationId id, Actor viewer, long afterSeq, int limit, CancellationToken ct = default);
    Task MarkReadAsync(Actor actor, ConversationId id, long seq, CancellationToken ct = default);
    Task<GuestInvite> InviteGuestAsync(Actor actor, ConversationId id, GuestContact contact, CancellationToken ct = default);
    Task CloseAsync(Actor actor, ConversationId id, CancellationToken ct = default);
}
public interface IGuestAccess { Task<GuestSessionResult> ExchangeAsync(string token, CancellationToken ct = default); }
public interface IConversationRetention { Task<DataSubjectExport> ExportAsync(ParticipantRef who, ...); Task<EraseOutcome> EraseAsync(ParticipantRef who, ...); }   // Erased | DeferredByHold
public interface IConversationExporter { Task<ExportedDocument> ExportAsync(ConversationSubject s, ExportMode mode, ...); }
```

Outcomes are enums with computed booleans, not bool flags. `CloseAsync` is idempotent; reopening is allowed until purge and clears `RetentionUntil`;
`MarkReadAsync` is monotonic and clamped to the conversation's `LastSeq`. Every method takes the `Actor`; the participant is **resolved from the actor inside the transaction** (same tenant and conversation, `LeftAt` null, kind matching) and is never passed in, and the order is access policy, then participant, then the idempotency read, then the `Seq` counter, then the inserts. The ids, `Actor`, `PostMessage` and the other placeholders get concrete types in the implementation plan, with the
`PublicAPI` files and the three engine projects.

## 10. Decisions, deviations and what is still open

**Deviation from the request: masking is applied when a message is read, not when it is written.** The request asked for masked
storage. Read-time masking keeps the original text for the data-subject export, the `Full` dossier and an erasure that must find
what a person wrote, lets a policy change apply to old messages, and lets each viewer type get its own projection from one stored
copy. The cost is that the unmasked text exists at rest; it is protected by the tenant predicate, the access policy and the audit
of §7, and a masked-at-write copy can be added later without a migration of meaning.


Decided: no realtime in v1 (polling plus batched notify, SignalR later); attachments are allow-listed, images re-encoded, stored under a tenant- and conversation-scoped key with the hash kept as a
column, with the virus scan a later row; the event port of §5 is the transaction-aware design, not the outbox; default retention 180 days after the subject closes, marked pending counsel
review; one conversation per subject in v1 (the unique key of §2 stands); guest links go over the guest's own signup channel,
sent by an app-supplied sender. **Still open for counsel:** the legal basis for each retention period, whether erasure or a legal hold wins when both apply, what a data-subject export may contain of other people's messages, and whether
a later "several conversations per subject" needs a `Kind` column in the unique key (a migration, not a redesign).
