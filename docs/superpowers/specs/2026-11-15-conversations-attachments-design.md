# Themia.Conversations: attachments on post (design note, no code)

Coord #0182, item 2 of the 2026-11-14 audit. Master spec: `2026-10-29-themia-conversations-design.md` §2, §6, §8 (this note only fixes what it left open).
Already in main: `conversation_attachments` table (3 engines), `MaxAttachmentsPerMessage` 10, `MaxAttachmentBytesPerMessage` 10 MiB,
`MaxAttachmentBytesPerConversation` 200 MiB, `IStorageProvider` registration, retention/erasure that delete attachment files then rows.
Missing: a way to attach on post, a way to download, orphan handling.

## 1. API (additive; `PostAsync` unchanged)

- `PostWithAttachmentsAsync(actor, tenantId, conversationId, PostMessage message, IReadOnlyList<AttachmentUpload> files, ct)` -> `PostResult` (+ new outcomes `AttachmentRejected`, `QuotaExceeded`).
  `AttachmentUpload(string FileName, string ContentType, Stream Content)`. Body may be empty when at least one file is present.
  A new method, not a new parameter: `IConversationService` already has adopter fakes; a default-interface method keeps them compiling.
- `OpenAttachmentAsync(actor, tenantId, conversationId, attachmentId, ct)` -> `AttachmentDownload(Outcome, Uri? ShortLivedUrl, string FileName, string ContentType, long Size)`.
  Gate: `ConversationAction.Read`, same participant check as `ListAsync`. Returns a presigned URL (default 60 s), never a public key.
- `ListAsync` message rows gain `Attachments` (id, file name, content type, size). Guest viewers see the same list.

## 2. Write order (the part that can leak)

1. Validate counts, per-file size, allow-listed content type (option `AllowedAttachmentContentTypes`, default images + pdf), sniff magic bytes (declared type must match).
2. Images re-encoded through `Themia.Imaging` (strips EXIF/GPS). Other types stored as is, `nosniff`, `Content-Disposition: attachment` on the presign request.
3. Write bytes to storage under `{tenant}/{conversation}/{attachmentId}` (attachmentId = fresh Guid) **before** the DB transaction, hash and size taken from the bytes actually written.
4. One DB transaction: the existing post path plus `INSERT conversation_attachments` rows, quota re-checked under the conversation counter lock (`SUM(size)` in the same transaction, so two parallel posts cannot both pass 200 MiB).
5. Any failure or Refused/Duplicate/Closed outcome after step 3 -> delete the just-written objects (best effort, logged).
6. Idempotent retry (same `ClientMessageId`) returns the stored message and deletes the freshly written duplicates; the stored rows win.

## 3. Orphans

A crash between 3 and 4 leaves objects with no row. Sweeper step in the existing retention job: list keys under a conversation prefix older than `AttachmentOrphanGrace` (default 1 h) with no row -> delete. Needs `IStorageProvider` list-by-prefix; if the provider has none, the orphan is documented and bounded by the per-conversation prefix (purge deletes the prefix, not only rows).

## 4. Not in this PR

Virus scan (later row, master spec §8), AspNetCore upload endpoint (item 5, separate PR), room export (item 4).

## 5. Tests that must go red without the check

Quota race (parallel posts over 200 MiB), declared-vs-sniffed type mismatch, EXIF stripped, Refused post leaves no object, duplicate ClientMessageId leaves one set of objects, download refused for a non-participant and for another tenant's id, erased person's attachment gone from storage and from the download, orphan sweep deletes only rows-less keys older than grace.

## 6. Questions for review

a) Presigned URL vs streaming through the app: presign leaks a bearer URL for 60 s; streaming costs the host bandwidth. Proposal: presign, short TTL. b) Re-encode failure on a corrupt image: reject the message, not store raw. c) Is list-by-prefix on `IStorageProvider` worth adding for orphans, or accept a documented bounded leak?

## 7. As built (after plan review, GO WITH CHANGES)

Supersedes the names and the orphan design above where they differ.

- API is a separate interface, `IConversationAttachments.PostAsync(..., files)` and `OpenAsync` (not `PostWithAttachmentsAsync` on `IConversationService`): adopter fakes of the main interface keep compiling. `OpenAsync` returns `OpenAttachmentResult(Outcome, Url, Attachment)`.
- **Orphans (answer to 6c):** no list-by-prefix. A staging table `conversation_attachment_uploads` holds each key, committed before the Put and deleted in the post transaction. Erasure, purge and the retention sweep (`AttachmentUploadGrace`, 1 h) work from rows, so every object that can exist is reachable from the database.
- The Put is outside the deadlock retry loop; cleanup uses `CancellationToken.None`; quota is pre-checked, then re-checked under the counter lock.
- Content types: explicit allow-list (`AllowedAttachmentContentTypes`, no wildcard, no SVG) and a magic-byte match. A corrupt image rejects the message (6b).
- `file_name` column added; the download is a presigned URL (60 s, max 15 min) with an attachment disposition (6a).
- Startup fails without an `IImageProcessor` or `IStorageProvider` (`AddThemiaConversationAttachments`).
- Found by the tests: `storage_key` of the staging table must carry the same binary collation as `conversation_attachments.storage_key`, or the purge/erasure UNION fails on SQL Server.
- Redaction: Redaction today happens only through erasure, which deletes the person's files with the message bodies (tested); there is no staff-redact call yet, and one added later must delete the files of the message it redacts.
- Known gap: an erasure racing an in-flight upload leaks one object only if the process also crashes.
