# Themia.Content

Versioned, bilingual content pages keyed by slug and language — legal and static pages with revision history,
stale-version refusal, and raw HTML and unsafe link destinations refused at write.

## Registration

```csharp
services.AddThemiaContent(options =>
{
    options.Languages.Add("th");
    options.Languages.Add("en");
    options.FallbackLanguage = "th";
});
services.AddThemiaContentPostgres(connectionString); // or AddThemiaContentMySql / AddThemiaContentSqlServer
```

MySQL requires 8.0.13 or later; MariaDB is not supported.

The engine method runs the schema migration immediately. Both tables are platform-level: there is no tenant column.

## Never write the content tables with SQL

Every rule lives in `IContentPageService`: a save writes a revision, advances the version, refuses a stale version
and checks the markdown. A migration or script that `INSERT`s or `UPDATE`s `content_pages` bypasses all of them. The
first revert or edit afterwards can then serve text that is in no revision — the defect coord #0132 describes in an
application that wrote its CMS content from migrations.

A migration also runs before `IContentPageService` can be resolved, so it could not call the service if it tried.

## Content shipped with code

Ship content through `SaveAsync` from a startup step that runs once the service resolves — a hosted service, or an
explicit call during boot. Give every content change a constant `AppliesToVersion`: the version the page must be at
before the change, 0 when the change creates the page.

```csharp
public sealed record ContentChange(string Slug, string Language, string Title, string Markdown, int AppliesToVersion);

public static async Task<bool> ApplyAsync(IContentPageService service, ContentChange change, CancellationToken ct)
{
    var page = await service.GetForEditAsync(change.Slug, change.Language, ct);
    if ((page?.CurrentVersion ?? 0) != change.AppliesToVersion)
    {
        return false; // already applied, or an editor has changed the page since
    }

    var result = await service.SaveAsync(new ContentPageSave(
        change.Slug, change.Language, change.Title, change.Markdown, page?.IsPublished ?? true,
        change.AppliesToVersion, "Shipped with application code", EditorId: "app"), ct);
    return result.Succeeded; // a Conflict means someone saved first: leave it
}

// Shipped in release 1:
new ContentChange("terms", "th", "ข้อกำหนด", termsV1, AppliesToVersion: 0);
// Shipped in release 2, rewording what release 1 created:
new ContentChange("terms", "th", "ข้อกำหนด", termsV2, AppliesToVersion: 1);
```

`AppliesToVersion` never changes after the change is written, and that constant is what keeps the step safe:
`SaveAsync` refuses a save whose `ExpectedVersion` is not the page's current version, so a re-run and a run after an
editor's save are both refused. The version check before the save only skips that round trip — don't pass the
version you just read as `ExpectedVersion`, or the guard passes every time.

## Rendering — the web half

Themia refuses raw HTML and unsafe link schemes at write. **The web renderer is still the safety boundary**: it must
drop raw HTML and refuse the same schemes again, because the write rule cannot fix rows saved before it existed.

Render with **`marked` 18.x and exactly this configuration**. The fixture's bytes were verified on `marked` 18.0.11
and 18.0.13; pin an exact version in your web app and re-run the fixture before upgrading. The golden fixture pins
its bytes; `markdown-it` matched on 5 of 12 inputs and cannot reproduce it.

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

`marked` passes `href` to the renderer undecoded: `[x](javascript&#58;alert(1))` arrives as `javascript&#58;alert(1)`,
which a browser decodes inside the attribute. `urlAllowed` decodes entities first for that reason.

### The fixture

Copy `tests/Themia.Content.Tests/Fixtures/markdown-dialect.json` from the Themia repository **byte for byte** and
assert `cmsMarkdown.parse(entry.markdown) === entry.html` for every entry. Every entry ships as `candidate`; report on
coord when your renderer reproduces them, and they are promoted to `confirmed` once both applications have.

## Importing from an existing schema

`IContentPageImporter` moves pages and their history from another system into empty content tables, once. Themia never
reads the other system's tables; you read them and hand over the rows. Every rule is checked before anything is
written, and any violation writes nothing:

| rule | refused as |
|---|---|
| the content tables already hold pages | `TargetNotEmpty` |
| a slug and language appear twice; an invalid slug or unconfigured language | `DuplicateKey`, `InvalidSlugOrLanguage` |
| a field longer than its column | `FieldTooLong` |
| revisions are not exactly 1 through the current version | `NonContiguousRevisions` |
| the page's title or body is not its current revision's | `ContentDiffersFromCurrentRevision` |

The last rule refuses rather than repairs. A page whose served text is in no revision has history that cannot be
trusted; record the served text as a new revision in the source system first, then import.

Two more things to know. A null page, revision, title or body is a programming error and throws `ArgumentException`
before anything is checked; an empty string is allowed. And the empty-table check and the writes share one READ
COMMITTED transaction, so two imports started at the same moment could both see empty tables — run the import once,
from one process. A page in the source system with no revision at its current version is refused as
`NonContiguousRevisions`: give it a revision holding its served text first.

For a PostgreSQL source shaped like propertiezy's `CmsPages` / `CmsPageRevisions`:

```csharp
const string PagesSql = """
    SELECT "Slug", "Language", "Title", "ContentMarkdown", "CurrentVersion", "IsPublished", "InsertDate", "UpdateDate", "UpdateUserId"
      FROM "CmsPages" ORDER BY "CmsPageId";
    """;
const string RevisionsSql = """
    SELECT p."Slug", p."Language", r."Version", r."Title", r."ContentMarkdown", r."ChangeSummary", r."CreatedUserId", r."CreatedDate"
      FROM "CmsPageRevisions" r JOIN "CmsPages" p ON p."CmsPageId" = r."CmsPageId"
     ORDER BY p."CmsPageId", r."Version";
    """;
```

Map each page row and its revisions to `ContentPageImport` / `ContentRevisionImport` (author `Guid`s become strings)
and call `ImportAsync`. `tests/Themia.Content.IntegrationTests/PropertiezyAdoptionTests.cs` in the Themia repository is
this recipe, run against a database that already holds rows.
