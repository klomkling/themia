# Themia.Conversations

A per-object chat for members and guests: tenant-scoped conversations, participants, messages and attachments. Design:
`docs/superpowers/specs/2026-10-29-themia-conversations-design.md`.

**Status: the store.** Schema, engine packages, options and the default-deny policy shipped first; this part adds `IConversationService` (open, join, post, list,
mark read, close) and the transaction-aware `IConversationEventSink`. Guests, notification delivery, retention and erasure, export and the ASP.NET Core
endpoints follow in later releases.

```csharp
services.AddThemiaConversations(o => o.DefaultRetention = TimeSpan.FromDays(180));   // limits + retention; default-deny policy
services.AddThemiaConversationsPostgres(connectionString);                           // dialect + runs the migration

var opened = await conversations.OpenAsync(actor, tenantId, new ConversationSubject("booking", "B-1"));
await conversations.JoinAsync(actor, tenantId, opened.ConversationId!.Value, "Somchai");
var posted = await conversations.PostAsync(actor, tenantId, opened.ConversationId.Value, new PostMessage(clientMessageId, "hello"));
```

- **Every call takes the `Actor` and the tenant.** Order inside a call: access policy, then the acting participant is resolved inside the transaction (this tenant
  and conversation, not left, kind matching the actor; a guest's `ParticipantId` is only a lookup key: build a guest actor only from a verified guest session), then the idempotency read, the sequence counter, the inserts.
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
