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

Render with **`marked` 18.x and exactly this configuration**. The golden fixture pins its bytes; `markdown-it`
matched on 5 of 12 inputs and cannot reproduce it.

```ts
import { Marked } from 'marked';

const ALLOWED_SCHEMES = new Set(['http', 'https', 'mailto', 'tel']);

function decodeEntities(s: string): string {
  return s
    .replace(/&#x([0-9a-f]+);?/gi, (_, h) => String.fromCodePoint(parseInt(h, 16)))
    .replace(/&#([0-9]+);?/g, (_, d) => String.fromCodePoint(parseInt(d, 10)))
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
  return !m || ALLOWED_SCHEMES.has(m[1].toLowerCase());
}

const EXPLICIT_ANCHOR = /\s*\{#([a-z0-9][a-z0-9-]*)\}\s*$/i;

export const cmsMarkdown = new Marked({
  renderer: {
    html: () => '',
    heading(token) {
      const m = EXPLICIT_ANCHOR.exec(token.text);
      const html = this.parser.parseInline(token.tokens);
      return m
        ? `<h${token.depth} id="${m[1].toLowerCase()}">${html.replace(EXPLICIT_ANCHOR, '')}</h${token.depth}>\n`
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
