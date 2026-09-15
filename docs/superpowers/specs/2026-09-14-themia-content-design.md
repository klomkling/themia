# Themia.Content — versioned, bilingual content pages

**Status:** approved design (brainstorming 2026-09-14), revised the same day after a `/scrutinize` pass — see
§15. Implemented on `feat/themia-content`; release pending (0.26.0).
**Target version:** `0.26.0`, released by **2026-11-01** — ezy-assets' needed-by date (coord #0130 [1]),
worked back from ezyassets.com launching in December 2026.
**Tracks:** coord #0130 (filed as `Themia.Modules.Content`), #0131.
**Renames:** the package requested as `Themia.Modules.Content` ships as the neutral **`Themia.Content`**
family. There is no module — see §2.
**Evidence:** propertiezy's production implementation (read at `origin/main` 94adf17, after its PR #262),
its reference renderer run as `marked` 18.0.11, `markdown-it` 14.3.2 run on the same inputs, and
propertiezy's own addendum on #0130 [3]. Every byte of expected HTML in this spec came from running a
renderer, not from reasoning about one.

**Scope change after coord agreement.** Coord #0130 [2] and [4] agreed that item 4 (adoption over an
existing schema) would ship in a follow-up release. The maintainer moved it into `0.26.0` on 2026-09-14.
That is more than propertiezy asked for, not less, but it puts work with no precedent in the repo on the
path ezy-assets is waiting for. The coord request must be updated to say so.

---

## 1. What this is

Two consumers need the same feature. Propertiezy has served its legal pages (`/terms`, `/privacy`,
`/about`, `/faq`, `/safety`, `/contact`) from a CMS since August. ezy-assets needs the same thing for
ezyassets.com: one platform-level set of legal and static pages, **not per tenant**, live on launch day.

Both run PostgreSQL + Dapper + FluentMigrator with SvelteKit on the web side. Neither is tenant-scoped for
this feature.

A page is keyed by `(slug, language)`. Every save writes an immutable revision and advances the page's
version. Revert is a save of an old revision's body. The public read serves the requested language and
falls back to a configured default when that language has no published page.

The part worth owning centrally is the part people get wrong: **raw HTML and dangerous URLs are refused at
write, and dropped again at render**, and **a stale editor cannot silently overwrite newer text**.

---

## 2. Package shape — and why there is no module

```
Themia.Content                net8.0;net10.0   Dapper + FluentMigrator + Markdig (parse only). No driver, no ASP.NET.
Themia.Content.PostgreSql     net8.0;net10.0   Npgsql dialect + registration
Themia.Content.MySql          net8.0;net10.0   MySqlConnector dialect + registration
Themia.Content.SqlServer      net8.0;net10.0   Microsoft.Data.SqlClient dialect + registration
Themia.Content.AspNetCore     net8.0;net10.0   optional public + admin endpoints
```

**No `Themia.Modules.Content`.** Every `Themia.Modules.*` project references `Themia.Framework.Core` and
`Themia.Framework.Data.Abstractions`, and `Modules.Pdf`, `.Notifications` and `.Messaging` additionally
reference `Microsoft.EntityFrameworkCore.Relational`. The request's own conditions rule that out: #0058,
#0071, #0116 and #0117 (a core that drags EF Core is not neutral) and #0098 (a tenant guard in a
single-org consumer refused every anonymous request). A module in Themia exists to carry tenant-scoped
state; this feature has none. Same per-capability call as `Themia.Geo`, `Themia.AI` and the
exception-logging family.

**Layout precedent is `Themia.Challenges`, not `Themia.Audit`.** Challenges' core carries Dapper and
FluentMigrator with no ASP.NET and no driver, exactly item 1 of the request. `Themia.Audit`'s core carries
a `Microsoft.AspNetCore.App` framework reference, which this core must not.

**All three engines ship in `0.26.0`**, although both current consumers run PostgreSQL. Every neutral
Dapper family in Themia (Audit, Challenges, Messaging, Exceptional, DataProtection) ships all three, and
multi-DB is a resolved decision in `CLAUDE.md`. The only family with fewer is `Themia.Framework.Data.EFCore`.

**No tenant scope in the schema**, stated explicitly as the request asked. There is no nullable
`tenant_id` column that nobody uses.

---

## 3. Public API — `Themia.Content`

```csharp
public interface IContentPageService
{
    Task<ContentPage?> GetPublishedAsync(string slug, string? language, CancellationToken ct = default);
    Task<ContentPage?> GetForEditAsync(string slug, string language, CancellationToken ct = default);
    Task<PagedResult<ContentPageSummary>> ListAsync(int page, int limit, CancellationToken ct = default);
    Task<PagedResult<ContentPageRevision>> GetRevisionsAsync(
        string slug, string language, int page, int limit, CancellationToken ct = default);
    Task<ContentSaveResult> SaveAsync(ContentPageSave save, CancellationToken ct = default);
    Task<ContentSaveResult> RevertAsync(ContentPageRevert revert, CancellationToken ct = default);
}

public sealed record ContentPageSave(
    string Slug, string Language, string Title, string Markdown, bool IsPublished,
    int ExpectedVersion, string? ChangeSummary, string? EditorId);

public sealed record ContentPageRevert(
    string Slug, string Language, int TargetVersion,
    int ExpectedVersion, string? ChangeSummary, string? EditorId);

public enum ContentSaveOutcome { Saved, Conflict, Invalid, NotFound }

public sealed class ContentSaveResult
{
    public ContentSaveOutcome Outcome { get; }
    public ContentPage? Page { get; }                              // Saved
    public int? CurrentVersion { get; }                            // Conflict — how far behind the editor is
    public string? CurrentUpdatedBy { get; }                       // Conflict — lets a client recognise its own write
    public DateTimeOffset? CurrentUpdatedAt { get; }               // Conflict
    public IReadOnlyList<ContentValidationError> Errors { get; }   // Invalid
    public bool Succeeded => Outcome == ContentSaveOutcome.Saved;
}

public sealed class ContentOptions
{
    public IList<string> Languages { get; }          // propertiezy and ezy-assets: th, en
    public string FallbackLanguage { get; set; }     // both: th
}

public static class ContentMarkdownRules
{
    public static IReadOnlyList<ContentMarkdownViolation> Check(string markdown);   // §7
}

public interface IContentPageImporter               // §10
{
    Task<ContentImportResult> ImportAsync(IReadOnlyList<ContentPageImport> pages, CancellationToken ct = default);
}
```

### Decisions in the API, and why

- **`ExpectedVersion` is a required `int`, never nullable.** A page that does not exist is version 0, so
  one number covers create and update. A nullable version lets a caller that forgets it overwrite someone
  else's text silently — the defect this feature exists to prevent.
- **`Language` has no default anywhere on the write path.** Propertiezy's request type defaulted it to
  Thai, and reverting the English page wrote the English revision over the Thai row; every revert test
  used the default language, where the wrong default happens to be right (#0130 [3], measured). Every
  write record carries every key dimension explicitly.
- **`EditorId` is a string.** Propertiezy's user ids are `Guid`, ezy-assets' are `int`. Same choice as
  `AuditEntry.ActorId`.
- **Invalid input is a result, not an exception.** Operational input errors map to 422 at the endpoint;
  exceptions stay for faults.
- **`GetForEditAsync` never falls back.** An admin opening the English page must see English or nothing;
  showing the Thai body invites saving it over the English row (the reference repository records the same
  reasoning).
- **Every list is paged**, even though content sets are tens of rows. `PagedResult<T>` is `Themia.Content`'s
  own (Items + Total); `Themia.Audit`'s lives in Audit's namespace and cannot be shared across packages.
- **Outcome is an enum with a computed `Succeeded`**, following `ChallengeIssueOutcome`: a state added later
  breaks every exhaustive switch instead of compiling silently.

### Validation

| field | rule |
|---|---|
| `Slug` | `^[a-z0-9]+(?:-[a-z0-9]+)*$`, at most 100 |
| `Language` | a member of `ContentOptions.Languages` after `Trim().ToLowerInvariant()`; the normalised value is what is stored and compared |
| `Title` | non-empty, at most 200 |
| `Markdown` | non-empty, no violation from `ContentMarkdownRules.Check` |
| `ChangeSummary` | at most 500 |
| `EditorId` | at most 256 |
| `ExpectedVersion` | 0 or greater |
| `TargetVersion` | 1 or greater |

`ContentOptions` is validated at registration: `Languages` non-empty and distinct; `FallbackLanguage` a
member of it.

---

## 4. Storage

```
content_pages
  id               bigint identity PK
  slug             varchar(100)   not null
  language         varchar(35)    not null
  title            varchar(200)   not null
  markdown         max text       not null
  current_version  int            not null
  is_published     boolean        not null
  created_at       timestamp      not null
  updated_at       timestamp      not null
  updated_by       varchar(256)   null
  UNIQUE ux_content_pages_slug_language (slug, language)

content_page_revisions
  id               bigint identity PK
  page_id          bigint         not null  FK fk_content_page_revisions_page -> content_pages.id (no cascade)
  version          int            not null
  title            varchar(200)   not null
  markdown         max text       not null
  change_summary   varchar(500)   null
  created_at       timestamp      not null
  created_by       varchar(256)   null
  UNIQUE ux_content_page_revisions_page_version (page_id, version)
```

- **Literal table names, identical on every engine, never `InSchema(...)`** — the rule
  `ChallengeSchemaMigration` records after `outbox_messages` collided across two modules on MySQL. Names are
  capability-prefixed (`content_*`, like `messaging_*`); ezy-assets has no table named like page, content
  or cms.
- **`bigint` identity keys**, following `Themia.Audit`. Nothing needs a client-generated id.
- **Timestamps:** `AsDateTimeOffset()` on PostgreSQL and SQL Server, `DATETIME(6)` on MySQL via
  `IfDatabase`, following `PdfTemplateSchemaMigration`. Every value comes from `TimeProvider.GetUtcNow()`,
  never from the database clock, following `ChallengeService`.
- **Long text** is `AsString(int.MaxValue)`, following `PdfTemplateSchemaMigration.body`.
- **No cascade and no delete API.** Nothing in either consumer deletes a page; revisions are history.
- **`language` is 35 characters**, the practical ceiling for a BCP 47 tag, so `zh-Hant` fits.

### Migration and ledger

One `ContentSchemaMigration` in the core assembly, run by each engine package's registration through
`ThemiaMigrations.Run`. The ledger is `ThemiaVersionTable` — `themia_version_<assembly>` per migration
assembly, verified in `ThemiaVersionTable.cs` — so the migration number cannot collide with a consumer's own
FluentMigrator `VersionInfo`. That is the class of defect behind the 15 days without `data_protection_keys`;
it is closed here by construction, not by picking an unusual number.

### Dialect

`IContentPageDialect` follows `IChallengeDialect`: `DbConnection CreateConnection()`, one SQL member per
statement, named Dapper parameters as the contract, plus one non-SQL member:

```csharp
bool IsDuplicateKey(DbException exception);   // 23505 · 1062 · 2627 / 2601
```

Driver error codes live in the engine packages, which reference the drivers; the core stays driver-free.
Unique-violation detection exists elsewhere in Themia only in `Themia.Framework.Data.Abstractions`
(`ISqlExceptionInterpreter`), which a neutral core cannot reference.

### Registration

```csharp
services.AddThemiaContent(o => { o.Languages.Add("th"); o.Languages.Add("en"); o.FallbackLanguage = "th"; });
services.AddThemiaContentPostgres(connectionString);   // or AddThemiaContentMySql / AddThemiaContentSqlServer
```

The engine method registers the dialect, calls `MigrationEngineRegistry.Add` (register-on-use, coord #0126),
runs the migration, and on PostgreSQL adds `AddPostgresSchemaProbe` for both tables. The probe is
PostgreSQL-only for the same reason it is in Challenges: `search_path` resolution is a PostgreSQL concern.
The core checks for a dialect lazily at first resolution of `IContentPageService`, so either call order works
and a missing engine still fails loudly.

---

## 5. Write path

Every write runs on **one connection and one transaction**, including revert. Propertiezy's revert opened a
transaction and then called a save that opened its own connection, so the transaction covered the lookups
and not the write (#0130 [3]).

**Every transaction is opened at `IsolationLevel.ReadCommitted`, on every engine.** PostgreSQL and SQL Server
default to it; MySQL's InnoDB defaults to REPEATABLE READ, where a plain `SELECT` reads from the snapshot taken
at the transaction's first read. Revert reads its target revision before the guarded `UPDATE`, so under
REPEATABLE READ the version it reads afterwards for the 409 is stale. Measured on mysql 8.4.9: the guarded
`UPDATE` correctly matched zero rows, and the follow-up plain `SELECT` returned version 1 while the committed
row was at 2; at READ COMMITTED it returned 2. The guard was right and the 409 was wrong. The repository already
opens MySQL transactions at READ COMMITTED, for a different reason (gap-lock deadlocks), in
`MySqlMessagingDialect`, `MySqlNotificationsDialect` and `SequenceProvider`.

### Save

1. Validate (§3). Any failure returns `Invalid` **before a connection is opened.**
2. Open the connection, begin the transaction.
3. **`ExpectedVersion == 0` — create.**
   Take a savepoint. `INSERT` the page at version 1.
   - Inserted: `INSERT` revision 1, commit, return `Saved`.
   - `IsDuplicateKey`: **roll back to the savepoint**, read the existing row's `current_version`, `updated_by`
     and `updated_at`, roll back,
     return `Conflict(currentVersion)`.
4. **`ExpectedVersion > 0` — update.**

   ```sql
   UPDATE content_pages
      SET title = @Title, markdown = @Markdown, is_published = @IsPublished,
          current_version = @ExpectedVersion + 1, updated_at = @Now, updated_by = @EditorId
    WHERE slug = @Slug AND language = @Language AND current_version = @ExpectedVersion
   ```

   - One row: `INSERT` revision `ExpectedVersion + 1`, commit, return `Saved`.
   - Zero rows: read `current_version`, `updated_by` and `updated_at` for `(slug, language)`. A row exists:
     `Conflict`.
     None: `NotFound`. Roll back.

### Why a savepoint, and why not the alternatives

- **Not catch-and-continue.** A failed statement aborts a PostgreSQL transaction, and `Conflict` must carry
  the winner's version, which needs one more query an aborted transaction cannot run (#0130 [3]).
- **Not `INSERT … ON CONFLICT DO NOTHING` on every engine.** That is PostgreSQL syntax. MySQL's nearest form,
  `ON DUPLICATE KEY UPDATE id = id`, reports **one affected row both for an insert and for a no-op on an
  existing row** when `CLIENT_FOUND_ROWS` is set — and MySqlConnector sets it by default
  (`UseAffectedRows=false`), so the row count cannot tell the two cases apart.
- **A savepoint gives one path on all three engines**: the insert attempt is the check, the unique index makes
  it atomic, and rolling back to the savepoint leaves the transaction able to read the winner.
  `DbTransaction.Save` / `Rollback(savepointName)` is the provider-neutral API. **That all three drivers
  support it is a requirement proven by the concurrent-create test on each engine (§12), not an assumption
  this spec relies on.** Reflection over the pinned drivers (Npgsql 10.0.3, MySqlConnector 2.6.0,
  Microsoft.Data.SqlClient 6.1.5) shows all three override `Save(string)` and `Rollback(string)`. **Do not gate
  on `DbTransaction.SupportsSavepoints`:** MySqlConnector and SqlClient do not override it, so it reports `false`
  on two engines that support savepoints.

### Two guards, neither of them a read-then-compare

The create guard is the unique index `ux_content_pages_slug_language`; the update guard is
`current_version = @ExpectedVersion` inside the `UPDATE`. **The `WHERE` compares against the editor's
version, never a value just read.** Propertiezy's first draft passed the read value, which made the SQL guard
true only because a code check had already run — remove the code check and the `WHERE` compared the row with
itself (#0130 [3]). There is no code check here to hide behind: each guard is the only thing that refuses its
case, so each is falsifiable on its own (§12).

`ux_content_page_revisions_page_version` is the backstop for any writer that is not this service: a second
revision with the same version fails to insert instead of existing.

### Revert

Same transaction as the save it performs:

1. Validate. Read the revision `(slug, language, TargetVersion)`. None returns `NotFound`.
2. Run the update path above with the revision's `title` and `markdown`, **`is_published` left as it is**,
   and `change_summary` defaulting to `Reverted to version {TargetVersion}`.

Revert carries `Language` explicitly and is refused on a stale `ExpectedVersion` exactly like a save.

### Publish

Publishing is a field of the save. **Every save writes a revision, including one that only toggles
`IsPublished`.** That matches the reference, so propertiezy's history maps one-to-one on import (§10).
Revisions do not store `is_published`.

### Content shipped with code — the same path, never SQL

Both consumers ship legal and static text with their releases: propertiezy has nine migrations that write CMS
content, and ezy-assets will write its pages before launch. The invariants in this section live only in
`IContentPageService`. **A migration that writes `content_pages` directly recreates, in Themia's own tables, the
defect §10 found in propertiezy's** — the seven migrations that changed served text without writing a revision.

So content that ships with code goes through `SaveAsync`, from a startup step that runs once the service is
resolvable (a hosted service, or an explicit call at boot) — never from a FluentMigrator migration, which runs
before the service exists. The package README says it outright: **never write the `content_*` tables with SQL.**

Each content change the application ships declares **`AppliesToVersion`** — the version the page must be at before
the change, 0 when the change creates the page. The pattern needs no new API:

1. `GetForEditAsync(slug, language)`; a missing page is at version 0.
2. The page is at the change's `AppliesToVersion`: `SaveAsync` with `ExpectedVersion = AppliesToVersion`. A
   `Conflict` means another instance or an editor saved first; stop.
3. The page is at any other version: the change has already applied, or an editor has changed the page. Leave it.

`AppliesToVersion` is a constant of the change, not a value that moves after each write, and the constant is what
makes the step safe: `SaveAsync` refuses any save whose `ExpectedVersion` is not the page's current version (the
update guard above). A "last written version" that advances would pass that guard on every run — a re-run would
save again, and a run after an editor's save would overwrite the editor. With a constant, both saves are refused.
Step 3's check only skips a save that would be refused anyway: it saves a round trip and is not what keeps shipped
content from overwriting an editor. Both are integration tests (§12).

---

## 6. Read path

**Published, with fallback — one query:**

```sql
SELECT ... FROM content_pages
 WHERE slug = @Slug AND is_published = true AND language IN (@Language, @Fallback)
 ORDER BY CASE WHEN language = @Language THEN 0 ELSE 1 END
 -- LIMIT 1 on PostgreSQL and MySQL, TOP 1 on SQL Server, per dialect
```

One query, not "try the language, then try the fallback": two queries are two round trips and a race in which
a page published between them changes which body the reader gets. A null or unknown `language` reads as the
fallback. A page that exists in the requested language but is unpublished falls back, as the reference does.

**For edit:** `slug = @Slug AND language = @Language`, published or not, no fallback.

---

## 7. Markdown rules — the write half

`ContentMarkdownRules.Check(markdown)` returns violations of two kinds: `RawHtml` and `DisallowedUrl`.

The markdown is **parsed with Markdig** (the CommonMark core pipeline, no extensions) and the rules run over
the syntax tree. Nothing is rendered on the server; Markdig is used only to know which bytes are code, which are
HTML, and which are link destinations.

1. **Refuse raw HTML:** any `HtmlBlock` or `HtmlInline` node — tags, comments, declarations and processing
   instructions alike. Code blocks (fenced or indented) and code spans are not HTML nodes, so documenting HTML
   inside code is accepted and renders escaped.
2. **Refuse destinations whose scheme is not allowed**, on every `LinkInline` (links and images),
   `AutolinkInline` and `LinkReferenceDefinition`. Normalise the way a browser does before deciding: decode HTML
   entities, strip ASCII whitespace and control characters, then allow **no scheme at all** (relative path,
   `/path`, `#fragment`, `?query`) or exactly **`http`, `https`, `mailto`, `tel`**. Markdig has already decoded
   entities in `Url`; decoding again can only make the check stricter, never looser.

### Why a parser and not regular expressions

The first draft of this rule removed code with regular expressions and scanned what was left. The `/scrutinize`
pass ran that rule against `marked` 18.0.11 and found it disagreeing with CommonMark's block structure in both
directions:

| input | regex write rule | `marked` render |
|---|---|---|
| an indented code block containing `<b>x</b>` | **reject** — the author is blocked on legitimate content | code |
| a fence closed by a longer fence, then `[x](javascript:alert(1))` | **accept** — the regex read the rest as code | link refused, silently |
| backticks on either side of a blank line around `<b>x</b>` | accept — stripped as one code span | HTML dropped, silently |

A set of regular expressions that removes code correctly is a CommonMark block parser, and this one was an
incomplete one. Markdig 1.3.0 (BSD-2-Clause; `net8.0`, `net10.0`, `netstandard2.0`) classified all thirteen probe
inputs — these three, plus raw HTML, a comment, an unclosed fence, entity-encoded and angle-bracket destinations,
a reference definition, a data image, an autolink and a fenced HTML example — the way `marked` renders them.
Markdig and `marked` are separate implementations and may still disagree on inputs nobody has tried; the fixture
is where such a case is pinned once found. **The render half remains the safety boundary:** in none of these
cases did `marked` emit a dangerous destination or a raw tag.

**Refused, not sanitised.** A sanitiser over markdown corrupts it (`a < b` comes back encoded, autolinks are
mangled, a fenced HTML example loses its content). Refusing tells the author what is wrong and leaves their
text as written. It is an allow-list by construction: no list of dangerous tags or schemes to keep current.

**The scheme allow-list is fixed, not configurable.** A configurable list would let the golden fixture (§8)
drift per consumer. Both consumers need exactly these four.

### What the reference missed — measured

Run against propertiezy's `MarkdownHtmlDetector` and its `marked` 18.0.11 renderer:

| input | reference write | reference render |
|---|---|---|
| `[click](javascript:alert(1))` | accept | `<a href="javascript:alert(1)">` |
| `![x](data:text/html;base64,...)` | accept | `<img src="data:text/html;...">` |
| `a <!-- hidden --> b` | accept | dropped |
| an unclosed fence containing `<b>` | **reject** | rendered as code |

The first two pass **both** halves. The claim that each half covers what the other cannot held for tags and
not for URLs: the detector only looks for tags, and `marked` does not filter `href` at all. The last two are
cases where the halves disagreed about the same input. All four are fixture entries (§8).

**`marked` passes `href` to the renderer undecoded.** `[x](javascript&#58;alert(1))` arrives as
`javascript&#58;alert(1)`, which a browser decodes inside the attribute. A scheme check that does not decode
entities first lets it through. Both halves therefore normalise before deciding.

---

## 8. Renderer contract and golden fixture

Themia does not ship a web renderer. Both SvelteKit apps render independently, so the dialect is pinned as
data, following `tests/Themia.Messaging.Hmac.Tests/Vectors/golden-vectors.json`.

### The renderer is `marked` 18.x with exactly this configuration

**Not `markdown-it`, and not "a markdown renderer".** `markdown-it` 14.3.2 produces byte-identical output to
`marked` 18.0.11 on **5 of 12** probe inputs — it differs even in the whitespace of a plain table. A fixture of
bytes is only meaningful against one renderer. ezy-assets' `package.json` lists `markdown-it` (added by a
security-audit bump in `21f187e4`, absent from the lockfile, imported nowhere in `src`); it must not be the CMS
renderer.

```ts
import { Marked } from 'marked';

const ALLOWED_SCHEMES = new Set(['http', 'https', 'mailto', 'tel']);

// An impossible code point decodes to U+FFFD, as a browser does. String.fromCodePoint throws on one,
// which would turn a saved page into a 500 for every reader (coord #0132).
const REPLACEMENT = String.fromCharCode(0xfffd);
const codePoint = (n: number): string => (n >= 0 && n <= 0x10ffff ? String.fromCodePoint(n) : REPLACEMENT);

function decodeEntities(s: string): string {
  return s
    .replace(/&#x([0-9a-f]+);?/gi, (_, h) => codePoint(parseInt(h, 16)))
    .replace(/&#([0-9]+);?/g, (_, d) => codePoint(parseInt(d, 10)))
    .replace(/&colon;/gi, ':')
    .replace(/&tab;/gi, '\t')
    .replace(/&newline;/gi, '\n')
    .replace(/&amp;/gi, '&');
}

export function urlAllowed(raw: string | null | undefined): boolean {
  const u = Array.from(decodeEntities(String(raw ?? '')))
    .filter((c) => { const n = c.codePointAt(0) ?? 0; return n > 0x20 && n !== 0x7f; })
    .join('');
  const m = /^([a-z][a-z0-9+.-]*):/i.exec(u);
  return !m || ALLOWED_SCHEMES.has(m[1]!.toLowerCase());
}

const EXPLICIT_ANCHOR = /\s*\{#([a-z0-9][a-z0-9-]*)\}\s*$/i;

export const cmsMarkdown = new Marked({
  renderer: {
    html: () => '',
    heading(token) {
      const m = EXPLICIT_ANCHOR.exec(token.text);
      const html = this.parser.parseInline(token.tokens);
      return m
        ? `<h${token.depth} id="${m[1]!.toLowerCase()}">${html.replace(EXPLICIT_ANCHOR, '')}</h${token.depth}>\n`
        : `<h${token.depth}>${html}</h${token.depth}>\n`;
    },
    link(token) {
      return urlAllowed(token.href) ? false : this.parser.parseInline(token.tokens);
    },
    image(token) {
      return urlAllowed(token.href) ? false : '';
    },
  },
});
```

Explicit anchors (`## Cookies {#cookies}`) exist because a page exists once per language and a section link
must work from either; slugifying heading text gives a different anchor per language.

### Fixture

`tests/Themia.Content.Tests/Fixtures/markdown-dialect.json`, one object per entry:

```json
{ "name": "entity-encoded colon", "status": "candidate",
  "markdown": "[x](javascript&#58;alert(1))", "write": "reject", "html": "<p>x</p>\n" }
```

- **Themia's suite asserts `write` for every entry** — confirmed and candidate alike. That is the half Themia
  ships.
- **`html` starts as `candidate` on every entry** and is promoted to `confirmed` only when both web apps
  reproduce it byte-for-byte with the configuration above — the lifecycle the HMAC vectors used (coord #0068,
  #0069). Themia's CI does not run Node and does not verify `html`.

### Seed entries

Produced by running the configuration above on `marked` 18.0.11. **Zero** outputs carried a `javascript:`,
`vbscript:` or `data:` destination. In the markdown column, `\t` and `\n` are escapes written as JSON would
store them, not literal characters.

| name | markdown | write | html |
|---|---|---|---|
| anchor | `## Cookies {#cookies}` | accept | `<h2 id="cookies">Cookies</h2>\n` |
| thai anchor | `## คุกกี้ {#cookies}` | accept | `<h2 id="cookies">คุกกี้</h2>\n` |
| https link | `[x](https://a.example/p?q=1#f)` | accept | `<p><a href="https://a.example/p?q=1#f">x</a></p>\n` |
| relative link | `[x](/privacy#cookies)` | accept | `<p><a href="/privacy#cookies">x</a></p>\n` |
| fragment link | `[x](#cookies)` | accept | `<p><a href="#cookies">x</a></p>\n` |
| colon in relative path | `[x](/a:b)` | accept | `<p><a href="/a:b">x</a></p>\n` |
| mailto autolink | `<mailto:privacy@a.example>` | accept | `<p><a href="mailto:privacy@a.example">mailto:privacy@a.example</a></p>\n` |
| email autolink | `<a@b.com>` | accept | `<p><a href="mailto:a@b.com">a@b.com</a></p>\n` |
| tel link | `[call](tel:+6621234567)` | accept | `<p><a href="tel:+6621234567">call</a></p>\n` |
| javascript link | `[x](javascript:alert(1))` | reject | `<p>x</p>\n` |
| javascript upper | `[x](JAVASCRIPT:alert(1))` | reject | `<p>x</p>\n` |
| entity-encoded colon | `[x](javascript&#58;alert(1))` | reject | `<p>x</p>\n` |
| named-entity colon | `[x](javascript&colon;alert(1))` | reject | `<p>x</p>\n` |
| angle destination | `[x](<javascript:alert(1)>)` | reject | `<p>x</p>\n` |
| reference definition | `[x][r]\n\n[r]: javascript:alert(1)` | reject | `<p>x</p>\n` |
| vbscript link | `[x](vbscript:msgbox(1))` | reject | `<p>x</p>\n` |
| data image | `![x](data:text/html;base64,PHNjcmlwdD4=)` | reject | `<p></p>\n` |
| tab in scheme | `[x](java\tscript:alert(1))` | accept | the source line as text in `<p>` — not parsed as a link |
| script inline | `a <script>x</script> b` | reject | `<p>a x b</p>\n` |
| html comment | `a <!-- hidden --> b` | reject | `<p>a  b</p>\n` |
| fence with html | a closed fence around `<b>x</b>` | accept | `<pre><code>&lt;b&gt;x&lt;/b&gt;\n</code></pre>\n` |
| unclosed fence | an unclosed fence before `<b>x</b>` | accept | `<pre><code>&lt;b&gt;x&lt;/b&gt;\n</code></pre>\n` |
| double-tick code | a double-backtick span containing a backtick and `<b>` | accept | one `<code>` element holding the backtick and an escaped `&lt;b&gt;` |
| html in inline code | a single-backtick span around `<br>` | accept | `<p>use <code>&lt;br&gt;</code> here</p>\n` |

The write verdicts above were produced by a JavaScript transcription of the first, regex-based draft of §7. The
Markdig-based rule must reproduce every one of them; thirteen of these inputs were checked against Markdig 1.3.0
during review, not all.

Five entries come from the `/scrutinize` probe, with `html` from the same `marked` 18.0.11 configuration. The
first three `write` verdicts are Markdig's; the last two are the regex transcription's, not yet run through Markdig.

| name | markdown | write | html |
|---|---|---|---|
| indented code with html | `para\n\n    <b>x</b>\n` | accept | `<p>para</p>\n<pre><code>&lt;b&gt;x&lt;/b&gt;\n</code></pre>\n` |
| longer closing fence, then javascript link | a fence opened with three backticks and closed with four, then `[x](javascript:alert(1))` | reject | `<pre><code>code\n</code></pre>\n<p>x</p>\n` |
| blockquote fence with html | `> ` before each line of a fence around `<b>x</b>` | accept | `<blockquote>\n<pre><code>&lt;b&gt;x&lt;/b&gt;\n</code></pre>\n</blockquote>\n` |
| entity-encoded tab in scheme | `[x](java&#9;script:alert(1))` | reject | `<p>x</p>\n` |
| hex entities without semicolons | `[x](&#x6A&#x61vascript:alert(1))` | reject | `<p>x</p>\n` |

The C# implementation is tested against the fixture file, not against these tables, and the fixture file is
generated from the probe runs rather than retyped from them.

---

## 9. `Themia.Content.AspNetCore`

Two mapping methods, so a consumer can mount them at different prefixes, or take neither and keep its own
controllers with the core and one engine package.

```csharp
RouteGroupBuilder MapThemiaContentPublicEndpoints(this IEndpointRouteBuilder endpoints, string prefix = "/pages");
  GET  {prefix}/{slug}?lang=                                   -> 200 | 404

RouteGroupBuilder MapThemiaContentAdminEndpoints(this IEndpointRouteBuilder endpoints,
    ContentAdminOptions options, string prefix = "/admin/content");
  GET  {prefix}/pages?page=&limit=
  GET  {prefix}/pages/{slug}/{language}
  PUT  {prefix}/pages/{slug}/{language}          { title, markdown, isPublished, expectedVersion, changeSummary }
  GET  {prefix}/pages/{slug}/{language}/revisions?page=&limit=
  POST {prefix}/pages/{slug}/{language}/revert   { version, expectedVersion, changeSummary }
```

`/api/v1` is the consumer's prefix to supply. Caching the public read is the consumer's decision, as the
request says.

### `ContentAdminOptions`

```csharp
public Func<HttpContext, Task<bool>>? Authorize { get; set; }       // fail-closed when unset
public Func<HttpContext, string?> ResolveEditorId { get; set; }     // default: the NameIdentifier claim
```

- **`Authorize` follows `AuditDashboardOptions`, `ExceptionalDashboardOptions` and `ThemiaQuartzOptions`:**
  unset means every admin route is refused, and mapping logs a warning. Authorization is supplied by the
  consumer — propertiezy gates on its own permission catalog, and Themia ships none. A policy is one
  `IAuthorizationService` call inside the delegate, and the returned `RouteGroupBuilder` still accepts
  `.RequireAuthorization(policy)`.
- **A refusal is 401 when unauthenticated and 403 when authenticated** — not the route-hiding 404 the
  dashboards use. This is a JSON API, not a UI whose existence is worth hiding.
- **`ResolveEditorId` defaults to `ClaimTypes.NameIdentifier`**, as `Themia.Quartz`'s dashboard reads it.

### Responses

Success uses `{ "data": ..., "meta": ... }`; lists carry `meta: { page, limit, total }`. Errors are RFC 7807.

| outcome | response |
|---|---|
| `Saved` | 200 `{ data: page }` |
| `Invalid` | 422 `ValidationProblem`, `RawHtml` and `DisallowedUrl` reported on field `markdown` |
| `Conflict` | 409 ProblemDetails with extensions `currentVersion`, `updatedBy`, `updatedAt` |
| `NotFound` | 404 ProblemDetails |

This is the one place Themia departs from its own neutral AspNetCore packages, which return bare bodies. It
follows the maintainer's API standard and propertiezy's `ApiResponse<T>`. ezy-assets returns bare bodies
elsewhere, but its CMS admin UI is not written yet, so nothing of theirs breaks.

**A retry cannot write twice, without an `Idempotency-Key`.** `PUT` and `POST .../revert` both carry
`expectedVersion`; a replayed request is refused with 409 instead of writing a second revision. That also means a
retry after a lost response reads as a conflict even when the lost request succeeded, and `currentVersion` alone
cannot tell the two cases apart. The 409 therefore carries `updatedBy` and `updatedAt` of the page as it now
stands, so a client can recognise its own write.

Request bodies reject unknown members (`JsonUnmappedMemberHandling.Disallow`). A consumer's form should keep
the author's text on 409 (SvelteKit: `fail()`, not `redirect`).

**Every body member except `changeSummary` is required** (`[JsonRequired]`); a missing one is a 400 ProblemDetails
and nothing is saved. That includes `isPublished`, which would otherwise bind `false` and unpublish a live page,
and `expectedVersion`: propertiezy's endpoint bound an omitted version as 0, which refuses an update but reports it
as a 409 that looks like another editor's save. A client that forgets the version is a client defect, and 400
says so. Decided by the maintainer before 0.26.0 (2026-09-16), because tightening it after release breaks clients.

---

## 10. Adoption over an existing schema — item 4

### Mechanism: an importer, not a migration

```csharp
public sealed record ContentPageImport(
    string Slug, string Language, string Title, string Markdown, int CurrentVersion, bool IsPublished,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? UpdatedBy,
    IReadOnlyList<ContentRevisionImport> Revisions);

public sealed record ContentRevisionImport(
    int Version, string Title, string Markdown, string? ChangeSummary, string? CreatedBy, DateTimeOffset CreatedAt);

public enum ContentImportOutcome { Imported, TargetNotEmpty, Invalid }

public enum ContentImportRule
{
    DuplicateKey,                        // (slug, language) appears twice in the input
    InvalidSlugOrLanguage,
    FieldTooLong,                        // Added during planning: without it, an over-long title fails inside the
                                          // transaction as a database error instead of being reported against its page.
    NonContiguousRevisions,              // not exactly 1..CurrentVersion
    ContentDiffersFromCurrentRevision,   // see "Why content must equal the last revision"
}

public sealed record ContentImportViolation(string Slug, string Language, ContentImportRule Rule);

public sealed class ContentImportResult
{
    public ContentImportOutcome Outcome { get; }
    public IReadOnlyList<ContentImportViolation> Violations { get; }   // every violation; Invalid only
    public int PagesFailingCurrentRules { get; }                       // reported, never refused
    public int RevisionsFailingCurrentRules { get; }
    public bool Succeeded => Outcome == ContentImportOutcome.Imported;
}
```

- **Themia's migration never reads or touches a consumer's tables.** `content_pages` is not `"CmsPages"`, so the
  #0085 / #0096 incident — a Themia migration meeting a table the consumer already created — cannot occur by
  construction. The adoption test in §12 proves it rather than asserting it.
- **Themia's code never names a consumer's tables.** The `SELECT` over `"CmsPages"` / `"CmsPageRevisions"`
  belongs to propertiezy, with a recipe in the package README. A consumer's schema is app knowledge; the scope
  guard keeps it out of Themia.
- **Everything is validated before the first row is written, in one transaction.** Any failure writes nothing.

### Rules

| rule | on failure |
|---|---|
| both target tables are empty — a one-time move, never a merge | `TargetNotEmpty` |
| `(slug, language)` unique across the input; slug and language pass §3 | `Invalid` |
| titles, change summaries and author ids fit their columns | `Invalid` (`FieldTooLong`) |
| revisions are `1..CurrentVersion`, contiguous, no duplicates | `Invalid` |
| **revision `CurrentVersion`'s title and markdown equal the page's, ordinal** | `Invalid` |

`Invalid` lists **every** violation with its `(slug, language)`, not the first one.

Version numbers, timestamps, actors (`Guid` becomes a string) and change summaries are preserved. Row ids are
not.

**`ContentMarkdownRules` is not applied to history.** The past cannot be edited, and the renderer half covers
display. The result reports how many imported pages and revisions would fail today's rules, so the consumer
knows what the next edit will be asked to fix.

### Why content must equal the last revision — found in propertiezy's production data

On propertiezy `origin/main`, only the two seed migrations insert revisions. **Seven later migrations change
page content directly** — `FillLegalPlaceholders`, `FillRealCompanyDetails`, `PersonalDataRequestsGoToPrivacy`,
`OneReportingChannelOnEveryPage`, `RewritePrivacyPolicy`, `FillLegalTermsFromCounsel`,
`OtpRowsHaveAPublishedPeriod` — **19 `UPDATE "CmsPages"` statements, zero revision inserts, zero version
bumps**, each limited to rows at `"CurrentVersion" = 1`.

So for those pages, the counsel-reviewed text being served **is in no revision**. Revision 1 is the seed
placeholder.

- An importer that builds the page from its latest revision would **publish the placeholder over counsel's
  text**.
- An importer that copies both as they are would store a page whose current revision is not its content:
  history diffs would lie, and a revert to the current version would change the page.

The importer therefore **refuses and reports**. That is propertiezy's own stance for its uniqueness migration,
which "refuses to run on duplicates rather than renumbering" legal-text history and asks the same of any data
move the module ships (#0130 [3]). Propertiezy resolves it on its side with a migration that records each page's
current content as a new revision — which also fixes the revert-to-placeholder defect in §13, whether or not it
ever imports.

Propertiezy's schema now carries `UC_CmsPageRevisions_Page_Version` (`M202609140001`); the import recipe and the
adoption test expect it.

---

## 11. Error handling and logging

- A duplicate-key exception on the page insert is the create conflict (§5). **Every other `DbException`
  propagates** — nothing is swallowed.
- `OperationCanceledException` propagates as cancellation, not as a failure.
- Every non-`Saved` path rolls the transaction back.
- A revision insert that fails after a successful guarded update is a fault — a writer outside the service broke
  the version sequence — and propagates.
- `ILogger<T>` only, structured: `slug`, `language`, `outcome`. **Markdown bodies are never logged.** `Conflict`
  is not a warning: a stale editor is normal use.

---

## 12. Testing

### `Themia.Content.Tests` — no database

- `ContentMarkdownRules` against the `write` verdict of **every** fixture entry.
- Validation, field by field. `Invalid` never opens a connection — asserted with a dialect double that counts
  `CreateConnection()`. That test proves only that no connection opens on invalid input; it says nothing about
  the SQL.
- Dialect contract, per engine: every SQL member binds the parameters the service passes (following
  `ChallengeDialectContractTests`).
- `ContentOptions` validation; the lazy dialect check at first resolution.

### `Themia.Content.IntegrationTests` — PostgreSQL, MySQL, SQL Server

One project, one container per engine per assembly (`[CollectionDefinition]`), `Category=Integration`.

- Create; update; stale update returns `Conflict` carrying the real current version; update of a missing page
  returns `NotFound`.
- **Concurrent updates at the same `ExpectedVersion`**: exactly one `Saved`, one `Conflict`, exactly one new
  revision.
- **Concurrent creates of the same `(slug, language)`**: one `Saved`, one `Conflict` carrying version 1. **This is
  the test that proves each driver supports savepoints and that the transaction can still read after a lost
  insert** (§5). On SQL Server, with the ADO.NET default `XACT_ABORT OFF`, it proves the savepoint calls
  themselves succeed, not that they are required for this outcome — see the remarks on
  `SqlServerContentDialect`.
- **Revert of a non-default language leaves the default language byte-identical** (#0130 [3]).
- **A revert that races a save reports the winner's version in its `Conflict`**, on MySQL above all: revert reads
  its target revision before the guarded `UPDATE`, the ordering that exposed REPEATABLE READ (§5).
- **Seeding (§5):** a seed step run twice leaves exactly one revision; a seed step run after an editor's save
  changes nothing.
- Revert is atomic, keeps `is_published`, refuses a stale `ExpectedVersion`; a missing target returns `NotFound`.
- Read: the requested language; the fallback; requested language unpublished falls back; neither returns null.
- The migration re-runs cleanly; the ledger is `themia_version_<assembly>`; the schema probe runs on PostgreSQL.
- A non-service insert of a duplicate `(page_id, version)` fails.

**Races are forced, not hoped for.** A `GatedContentDialect` wraps the real dialect and places a two-party
`Barrier` behind hooks that run when the service reads the guarded statement's SQL, following
`RaceGatingChallengeDialect`, whose remarks record that a bare `Task.WhenAll` did not reliably make two calls
overlap. It works identically on all three engines.
Propertiezy's lock-waiter polling on `pg_locks` is deterministic too, but PostgreSQL-only.

### `Themia.Content.AspNetCore.Tests` — a `TestServer` host

Unset `Authorize` refuses every admin route; 401 versus 403; 422 on field `markdown`; 409 carries
`currentVersion`; the envelope shape; an unknown request member is refused; `EditorId` from the default claim
and from an override.

### Adoption test (§10)

On PostgreSQL: create propertiezy's tables exactly as its migrations leave them — PascalCase columns,
`UC_CmsPages_Slug_Language`, `UC_CmsPageRevisions_Page_Version` — and seed rows **including a page whose content
was changed without a revision**. Then:

1. `AddThemiaContentPostgres`: Themia's tables exist, and **propertiezy's tables and rows are byte-identical**.
2. Import through the README recipe: `Invalid`, naming exactly the diverged page; nothing written.
3. Apply the "record current content as a revision" fix, then import: `Imported`.
4. The public read returns the source page's markdown byte-for-byte.

The importer's validation and write path run on all three engines.

### Acceptance: every guard must be seen failing

Each test below must turn red when its guard is removed, and the plan records the run:

| remove | must turn red |
|---|---|
| `AND current_version = @ExpectedVersion` | stale update; concurrent updates |
| `ux_content_pages_slug_language` | concurrent creates |
| `ux_content_page_revisions_page_version` | non-service duplicate revision |
| `Language` carried into the revert's save | non-default-language revert |
| the content-equals-last-revision import rule | adoption test, step 2 |
| the whitespace and control-character strip in the URL rule | fixture entry "entity-encoded tab in scheme" |
| `IsolationLevel.ReadCommitted` on the transaction | a revert racing a save, on MySQL |

Entity decoding is deliberately not in this table: Markdig decodes `Url` before the rule sees it, so removing the
rule's own decoding would turn nothing red — a guard listed here that cannot fail would be evidence of nothing.

A guard that cannot be made to fail is not evidence of anything; three have been found in this repository
already.

---

## 13. Findings handed back to consumers

Not Themia work. Both were posted to propertiezy as coord #0132 on 2026-09-14.

1. **propertiezy production: `javascript:` and `data:` destinations pass both halves** (§7). Only admins author
   pages, which bounds the exposure, but the pair's stated guarantee does not hold for URLs. Not touched by
   PR #262.
2. **propertiezy production: a revert to version 1 serves the seed placeholder** on every page one of the seven
   content migrations changed (§10), because the served text was never written as a revision.

---

## 14. Release

`0.26.0` contains the five packages, the fixture with every `html` at `candidate`, the importer, and the adoption
test — subject to the checkpoint below.

### Build order and the 2026-10-15 checkpoint

Agreed on coord #0130 [8]. The implementation plan orders the work in five blocks, and block 5 depends on nothing
ezy-assets needs:

1. Core, with PostgreSQL as the engine that proves it.
2. The MySQL and SQL Server engines and their integration suites.
3. `Themia.Content.AspNetCore`.
4. The golden fixture.
5. Item 4 — `IContentPageImporter` and the adoption test.

Themia posts a go/no-go on #0130 **no later than 2026-10-15**, for a release on 2026-11-01:

- **GO** — blocks 1–5 ship in `0.26.0`.
- **GO WITHOUT ITEM 4** — blocks 1–4 are on track and block 5 is not; `0.26.0` ships blocks 1–4 and the importer
  follows in the next release. `IContentPageDialect` already carries the import statement from block 1, so the
  interface does not change when block 5 lands later.
- **NO-GO** — blocks 1–4 are not on track, early enough for ezy-assets to build a stopgap.

The checkpoint is a commitment to answer, not to the date.

Registration points: `Themia.sln`; `CHANGELOG.md`; the package table in `README.md`;
`docs/themia-architecture-overview.md` (a §B row, the layered-architecture block, the specs index); the layered
block in `CLAUDE.md`. No workflow, Dependabot manifest or CI package list names neutral packages individually —
verified against every place `Themia.Challenges.PostgreSql` is referenced outside `src/`. The new `Markdig`
`PackageVersion` needs no manifest edit: `.github/dependabot-nuget/Manifest.csproj` references
`@(PackageVersion)`, so `verify-coverage.sh` sees it automatically. **`MIGRATION.md` gets no entry**: it lists
breaking changes only ("Non-breaking changes are not listed here"), and this release adds packages.

---

## 15. What earlier input got wrong

- **The request's example fixture bytes.** `a <script>x</script> b` was shown as `<p>a  b</p>`; `marked` 18.0.11
  produces `<p>a x b</p>` — the tags are dropped and the text between them is kept.
- **"Both halves are needed" was true and incomplete.** Neither half checked URLs (§7).
- **The halves disagreed** on unclosed fences and HTML comments (§7).
- **"Both web apps copy the fixture byte-identically"** assumed one renderer. ezy-assets lists a different one,
  which matches on 5 of 12 inputs (§8).
- **This spec's own first draft of the create race** caught the unique violation, rolled back and returned
  `Conflict`. On PostgreSQL that cannot return the winner's version: the failed statement aborts the transaction
  (#0130 [3]). Its replacement, an engine-specific insert-if-absent, was dropped because MySQL's row count cannot
  tell an insert from an existing row under `CLIENT_FOUND_ROWS`. The savepoint in §5 is the third version.
- **Item 4 was scoped out, then back in** (header).
- **The first draft's markdown rule was a set of regular expressions**, and it disagreed with CommonMark in both
  directions (§7). Replaced by Markdig's syntax tree.
- **The first draft named no isolation level.** Under MySQL's default REPEATABLE READ a revert's 409 reported the
  editor's own version back to it — measured, not inferred (§5).
- **The first draft enforced every invariant inside the service and offered no path for content shipped with
  code**, which is how both consumers deliver legal text. §5 now names that path.
- **The review's own first check of propertiezy's content counted 46 `<b>` tags** — every one a C# XML doc
  comment. With comment lines excluded, propertiezy's CMS content contains no raw HTML and only `https` and
  `mailto` links, so adoption does not lock its editors out of saving.

---

## 16. Out of scope

Page content (per product, counsel-reviewed, never moves); the admin UI; caching; tenant scope; deleting pages;
rendering markdown on the server; a configurable URL scheme list; a Themia CI job that runs Node.

## 17. Decisions — do not relitigate

- A neutral `Themia.Content` family, no module.
- All three engines in the first release.
- Rules once in `IContentPageService`; SQL per engine in `IContentPageDialect`.
- `ExpectedVersion` is a required `int`; `Language` is never defaulted on a write.
- The create guard is the unique index behind a savepoint; the update guard is the `WHERE` against the editor's
  version.
- Every save, including a publish toggle, writes a revision.
- Raw HTML and non-allow-listed URL schemes are refused at write and dropped at render; the four schemes are fixed.
- The renderer is `marked` 18.x with the §8 configuration.
- Themia's suite verifies `write`; `html` is promoted by the consumers.
- Admin authorization is a consumer delegate, fail-closed; refusals are 401 or 403.
- `{data, meta}` and RFC 7807.
- Adoption is an importer the consumer feeds; it refuses divergent history rather than repairing it.
- Markdown rules run over Markdig's syntax tree; nothing is rendered on the server.
- Every transaction runs at READ COMMITTED.
- Content that ships with code goes through `SaveAsync`, never SQL.
