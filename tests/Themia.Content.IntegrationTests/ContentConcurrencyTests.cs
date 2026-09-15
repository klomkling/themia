using System.Data.Common;
using Dapper;
using Xunit;
using static Themia.Content.IntegrationTests.ContentTestData;

namespace Themia.Content.IntegrationTests;

public abstract class ContentConcurrencyTests(ContentEngineFixture fixture)
{
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ConcurrentUpdates_ShouldSaveExactlyOnce_WhenBothExpectTheSameVersion()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0));
        using var barrier = new Barrier(2);
        var gated = new GatedContentDialect(fixture.Dialect)
        {
            BeforeUpdatePage = () => Assert.True(barrier.SignalAndWait(GateTimeout), "peer never reached the gate"),
        };
        var first = fixture.NewService(gated);
        var second = fixture.NewService(gated);

        var results = await Task.WhenAll(
            Task.Run(() => first.SaveAsync(Save(slug, "th", 1, "# A", editorId: "a"))),
            Task.Run(() => second.SaveAsync(Save(slug, "th", 1, "# B", editorId: "b"))));

        Assert.Single(results, r => r.Outcome == ContentSaveOutcome.Saved);
        var conflict = Assert.Single(results, r => r.Outcome == ContentSaveOutcome.Conflict);
        Assert.Equal(2, conflict.CurrentVersion);
        Assert.Equal(2, (await fixture.Service.GetRevisionsAsync(slug, "th", 1, 20)).Total);
    }

    [Fact]
    public async Task ConcurrentCreates_ShouldSaveExactlyOnce_AndReportVersionOneToTheLoser()
    {
        var slug = NewSlug();
        using var barrier = new Barrier(2);
        var gated = new GatedContentDialect(fixture.Dialect)
        {
            BeforeInsertPage = () => Assert.True(barrier.SignalAndWait(GateTimeout), "peer never reached the gate"),
        };
        var first = fixture.NewService(gated);
        var second = fixture.NewService(gated);

        var results = await Task.WhenAll(
            Task.Run(() => first.SaveAsync(Save(slug, "th", 0, "# A"))),
            Task.Run(() => second.SaveAsync(Save(slug, "th", 0, "# B"))));

        Assert.Single(results, r => r.Outcome == ContentSaveOutcome.Saved);
        var conflict = Assert.Single(results, r => r.Outcome == ContentSaveOutcome.Conflict);
        Assert.Equal(1, conflict.CurrentVersion);
        Assert.Equal(1, (await fixture.Service.GetRevisionsAsync(slug, "th", 1, 20)).Total);
    }

    [Fact]
    public async Task RevertThatLosesARace_ShouldReportTheWinnersVersion()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0, "# One"));
        await fixture.Service.SaveAsync(Save(slug, "th", 1, "# Two"));

        using var revertReadsDone = new ManualResetEventSlim();
        using var rivalCommitted = new ManualResetEventSlim();
        var reverter = fixture.NewService(new GatedContentDialect(fixture.Dialect)
        {
            // Reached after revert's two plain reads and before its guarded UPDATE.
            BeforeUpdatePage = () =>
            {
                revertReadsDone.Set();
                Assert.True(rivalCommitted.Wait(GateTimeout), "rival save did not commit");
            },
        });

        var revert = Task.Run(() => reverter.RevertAsync(new ContentPageRevert(slug, "th", 1, 2, null, "reverter")));
        Assert.True(revertReadsDone.Wait(GateTimeout), "revert never reached its update");
        var rival = await fixture.Service.SaveAsync(Save(slug, "th", 2, "# Three", editorId: "rival"));
        rivalCommitted.Set();
        var result = await revert;

        Assert.Equal(ContentSaveOutcome.Saved, rival.Outcome);
        Assert.Equal(ContentSaveOutcome.Conflict, result.Outcome);
        Assert.Equal(3, result.CurrentVersion);
        Assert.Equal("rival", result.CurrentUpdatedBy);
    }

    [Fact]
    public async Task DuplicateRevision_ShouldBeRefusedByTheSchema_WhenWrittenOutsideTheService()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0));

        await using var connection = fixture.Dialect.CreateConnection();
        await connection.OpenAsync();
        var pageId = await connection.ExecuteScalarAsync<long>(
            "SELECT id FROM content_pages WHERE slug = @Slug AND language = @Language", new { Slug = slug, Language = "th" });

        await Assert.ThrowsAnyAsync<DbException>(() => connection.ExecuteAsync(fixture.Dialect.InsertRevisionSql, new
        {
            PageId = pageId,
            Version = 1,
            Title = "Duplicate",
            Markdown = "# Duplicate",
            ChangeSummary = (string?)null,
            CreatedAt = fixture.Time.GetUtcNow(),
            CreatedBy = (string?)null,
        }));
    }
}

[Collection(PostgresContentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PostgresContentConcurrencyTests(PostgresContentFixture fixture) : ContentConcurrencyTests(fixture);

[Collection(MySqlContentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MySqlContentConcurrencyTests(MySqlContentFixture fixture) : ContentConcurrencyTests(fixture);

[Collection(SqlServerContentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SqlServerContentConcurrencyTests(SqlServerContentFixture fixture) : ContentConcurrencyTests(fixture);
