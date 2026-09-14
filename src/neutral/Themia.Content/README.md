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

`AppliesToVersion` never changes after the change is written. Re-running the step is a no-op, and a change never
overwrites an editor's save.
