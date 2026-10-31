# Themia.Conversations

A per-object chat for members and guests: tenant-scoped conversations, participants, messages and attachments. Design:
`docs/superpowers/specs/2026-10-29-themia-conversations-design.md`.

**Status: the store and guests.** Schema, engine packages, options and the default-deny policy shipped first; this part adds `IConversationService` (open, join, post, list,
mark read, close) and the transaction-aware `IConversationEventSink`. Guest invites, the token exchange and read-time masking are in, and so are the retention sweep, legal hold and per-person erasure (see the last section); self-service re-invite, notification delivery, export and the ASP.NET Core
endpoints follow in later releases.

```csharp
services.AddThemiaConversations(o => o.DefaultRetention = TimeSpan.FromDays(180));   // limits + retention; default-deny policy
services.AddThemiaConversationsPostgres(connectionString);                           // dialect + runs the migration

var opened = await conversations.OpenAsync(actor, tenantId, new ConversationSubject("booking", "B-1"));
await conversations.JoinAsync(actor, tenantId, opened.ConversationId!.Value, "Somchai");
var posted = await conversations.PostAsync(actor, tenantId, opened.ConversationId.Value, new PostMessage(clientMessageId, "hello"));
```

- **Every call takes the `Actor` and the tenant.** Order inside a call: access policy, then the acting participant is resolved inside the transaction (this tenant
  and conversation, not left, kind matching the actor; a guest is proved by the hash of the `GuestSessionKey` it holds: unexpired, not revoked, participant not left, this tenant and conversation, checked inside the same transaction), then the idempotency read, the sequence counter, the inserts.
  A denied call, a stranger and another tenant's conversation all answer `Refused`.
- **Post is ordered and idempotent.** `Seq` is dense per conversation: the counter row lock serialises posters (PostgreSQL `RETURNING`, SQL Server `OUTPUT`, MySQL
  `LAST_INSERT_ID(expr)`). The same `ClientMessageId` returns `Duplicate` with the original message and overwrites nothing. A deadlock or lock timeout retries the whole
  transaction up to three times.
- **Events.** The default `IConversationEventSink` writes a `conversation_events` row (ids and `Seq`, never the text) in the post transaction, so a failed post leaves none;
  register your own sink to enlist elsewhere. Draining and debouncing the rows is a later part.
- **Padded keys are refused.** Tenant, subject, user reference and client message id with leading or trailing whitespace throw `ArgumentException`: MySQL and SQL Server
  compare strings without trailing spaces, PostgreSQL does not.
- **Member and partner references never collide.** `user_ref` is stored with a kind prefix (`m:` member, `p:` partner), so member 42 and partner 42 are two participants; a data-subject lookup by reference must use the same form. A reference may be at most 198 characters.
- **Limits.** The body cap counts `string.Length` (UTF-16 units). The per-participant post rate limit belongs to the ASP.NET Core part, with the other limits.
- **System actors** are not resolved to a participant yet (system lines arrive with the retention part).

- **Access policy.** `IConversationAccessPolicy` denies everything until you register your own; every later service call asks it first.
- **Tenant isolation is in the schema.** Every table carries `tenant_id`; each child binds its parent on `(tenant_id, conversation_id, ...)`, so a row cannot
  point at another tenant's, or another conversation's, participant or message. The compared string columns use a byte-exact collation on MySQL and SQL Server
  (`Acme` and `acme` are different tenants).
- **Retention is a setting, pending counsel review:** 180 days after the subject closes by default, per `SubjectType` if you list it. Do not treat the number as decided.
- **MariaDB is not supported**, like the rest of Themia.

## Guests

A guest is a participant invited by contact (e-mail, E.164 phone or LINE id). The app sends the link; the core only issues the token.

```csharp
services.AddThemiaChallenges(o => o.ConfigureConversationGuestPurpose(TimeSpan.FromHours(72)));   // + one Challenges engine registration
services.AddThemiaConversationGuests(o =>
{
    o.Peppers["2026-10"] = pepperBytes;      // >= 32 random bytes from your secret store
    o.CurrentPepperId = "2026-10";
});

var invite = await guests.InviteAsync(memberActor, tenantId, conversationId, new GuestContact(GuestContactKind.Email, "guest@example.com"), "Guest");
// hand invite.Token!.Reveal() to your sender; never store or log it (Token and the contact have no property that returns their text, so logging an invite or a contact does not write them out)
var session = await guestAccess.ExchangeAsync(tenantId, tokenFromThePostedForm);          // POST only
var actor = ConversationActor.ForGuest(new GuestSessionKey(cookieValue));
```

- **The session is the only way to act as a guest.** `ExchangeAsync` redeems the token (one use) and returns a 256-bit key once; the store keeps only its SHA-256. Every call
  with a guest actor re-proves the session in the same statement, so leaving the conversation, closing it, expiry or revocation end access at once. `GuestSessionKey` has
  no member that returns the text, so logging or serialising an actor does not leak it. Session length is `SessionLifetime` (default 7 days, an interim value).
- **Redeem from a POST, never a GET:** mail and chat link previewers fetch URLs and would spend the token. Move the token out of the URL, send `Cache-Control: no-store`
  and `Referrer-Policy: no-referrer`, and rate-limit the endpoint by address and tenant (the `.AspNetCore` part will).
- **Revocation is read-then-write.** A post already in flight can commit after a concurrent redeem revoked its key; the next call is refused.
- **One live link per guest.** A re-invite supersedes the earlier link ("the old link stopped working" is expected). The guest's earlier sessions end when the new link is
  redeemed, not when it is issued, so someone who only knows a contact cannot log the guest out. Every failed exchange is one `Refused`.
- **Limits are layered.** `Themia.Challenges` throttles per participant. The core adds `MaxGuestsPerConversation` (default 50). Per-contact and per-inviter delivery
  throttling (SMS pumping) belongs to the sender you write.
- **Contacts are hashed, never stored.** HMAC-SHA256 under a versioned pepper over `kind:value`; an e-mail is lower-cased, a phone must be E.164 (a national form would make
  one person two guests), a LINE id keeps its case. **Never remove a pepper id while any row carries it:** the guest would not be found, a re-invite would add a second
  participant and a data-subject lookup would miss the person. Rotate by adding an id and making it current; a guest is re-hashed when next invited.
- **Masking is a read-time projection** (`IConversationMasker`, default regex): for a guest viewer e-mail, phone numbers and handles in the text are replaced and staff show
  their role label (or "Staff"). Best effort, not a control. If the masker throws, the body is hidden (`[message hidden]`), never shown raw. The stored text stays whole.
- A guest who left is not re-added by an invite; a call to add them back arrives with the retention part.

## Retention, legal hold and erasure

```csharp
services.AddSingleton<ILegalHoldPort, MyHoldPort>();   // the app's authority on holds; or o.NoLegalHoldPort = true to opt out
services.AddSingleton<IStorageProvider>(storage);       // the attachment files; deleted before their rows

var swept = await retention.SweepAsync(tenantId, new SweepOptions(BatchSize: 100));   // schedule it per tenant
await retention.PlaceHoldAsync(staffActor, tenantId, conversationId);                // policy action Hold
var erased = await dataSubject.ErasePersonAsync(dsrActor, tenantId, PersonRef.ForMember("42"));   // policy action Erase
```

- **Sweep.** `IConversationRetention.SweepAsync` moves closed conversations past `retention_until` to `Expired` (`OpenAsync` already answers `PendingPurge` for them), then
  purges the expired ones, at most `BatchSize` of each per call: call again to continue. It returns `Expired`, `Purged`, `HeldSkipped` and `Failed` counts.
- **No hold port, no purge.** There is no default `ILegalHoldPort`: one that said "not held" would purge under a hold. Without a port the sweep still marks `Expired`, purges
  nothing, logs one warning and returns `SweepOutcome.HoldPortMissing`. Register a port, or set `ConversationOptions.NoLegalHoldPort` to say you have none. A port that throws
  counts as held-or-unknown: the conversation is kept and counted in `Failed`.
- **Purge protocol.** The port is asked just before the conversation is claimed (`Expired` to `Purging`; a port answer of "held" makes it `LegalHold` and skips it). The files
  are then deleted outside any transaction (`IStorageProvider.DeleteAsync` is idempotent; the key is the stored `storage_key` verbatim). One transaction then re-verifies
  `Purging`, writes the tombstone (subject, message and attachment counts), deletes attachments, guest sessions, messages, participants, events and finally the conversation (so
  the subject opens a new one), and raises `ConversationLifecycleKind.Purged`. A failed file delete or database step leaves the conversation `Purging`; the next sweep resumes it
  without asking the port again. A `Purging` conversation cannot be reopened, joined or held (`HoldOutcome.PurgeInProgress`).
- **The accepted race.** A hold the app places between the port answering "not held" and the claim is not seen. Place holds through `PlaceHoldAsync` as well (it locks the row and
  sets `LegalHold`), and keep the port the source of truth at sweep time. `ReleaseHoldAsync` returns the conversation to `Active` and leaves `retention_until`, so the next sweep
  may expire it at once. The package never lifts a hold by itself.
- **Erasure.** `IConversationDataSubject.ErasePersonAsync` finds the person in the tenant (a member or partner by user reference, which keeps the `m:`/`p:` prefix apart; a guest
  by the hash of the contact they were invited with, trying every configured pepper) and, per conversation: deletes their attachment files, then in one transaction removes
  their attachment rows and guest sessions, redacts their messages (`body` NULL, `redacted_at` set; sequence numbers and the other participants' rows are unchanged) and
  anonymises the participant (display name `Erased participant`, user reference and contact hash cleared, marked left). It is idempotent: a second call erases nothing and
  returns `Erased`. A person who joins again afterwards is a new participant.
- **Holds defer erasure.** A conversation under legal hold or being purged is reported in `DeferredConversationIds` (outcome `PartiallyErased` or `Deferred`) and untouched; the
  package keeps no queue, so the app's DSR record re-issues the call once the hold is lifted. A file that cannot be deleted lands in `FailedConversationIds`; its rows are untouched.
  If a hold lands after the files were deleted but before the transaction, the conversation is deferred with its files already gone (the accepted race again).
- **Lifecycle events.** `IConversationLifecycleSink` receives `Purged`, `ParticipantErased`, `HoldPlaced` and `HoldReleased` inside the transaction that made the change (ids and
  counts, never text, names or contacts). The default does nothing: `conversation_events` is not used, because a purge deletes it. Register your own sink for an audit trail;
  a sink that throws rolls the change back.
- **A post re-checks its participant** under the counter lock, so a person erased (or gone) while their post waited leaves no message behind; the post answers `Refused`.
