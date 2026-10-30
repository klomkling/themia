# Themia.Conversations

A per-object chat for members and guests: tenant-scoped conversations, participants, messages and attachments. Design:
`docs/superpowers/specs/2026-10-29-themia-conversations-design.md`.

**Status: schema and types only.** This part ships the whole database schema (seven tables, in one migration), the engine packages
(`Themia.Conversations.PostgreSql`, `.MySql`, `.SqlServer`), the options, and a default-deny access policy. The store (open, post, list, mark read, close),
guests, retention, export and the ASP.NET Core endpoints follow in later releases; nothing here sends or stores a message yet.

```csharp
services.AddThemiaConversations(o => o.DefaultRetention = TimeSpan.FromDays(180));   // limits + retention; default-deny policy
services.AddThemiaConversationsPostgres(connectionString);                           // dialect + runs the migration
```

- **Access policy.** `IConversationAccessPolicy` denies everything until you register your own; every later service call asks it first.
- **Tenant isolation is in the schema.** Every table carries `tenant_id`; each child binds its parent on `(tenant_id, conversation_id, ...)`, so a row cannot
  point at another tenant's, or another conversation's, participant or message. The compared string columns use a byte-exact collation on MySQL and SQL Server
  (`Acme` and `acme` are different tenants).
- **Retention is a setting, pending counsel review:** 180 days after the subject closes by default, per `SubjectType` if you list it. Do not treat the number as decided.
- **MariaDB is not supported**, like the rest of Themia.
