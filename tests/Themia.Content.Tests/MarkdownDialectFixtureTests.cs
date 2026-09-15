using System.Text.Json;
using Xunit;

namespace Themia.Content.Tests;

// The contract both consumer web apps copy. A failure here means Themia's write rule and the pinned renderer
// disagree about an input — a spec decision, not an expected value to adjust.
public class MarkdownDialectFixtureTests
{
    public sealed record Entry(string Name, string Status, string Markdown, string Write, string Html);

    private static IReadOnlyList<Entry> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "markdown-dialect.json");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("entries").EnumerateArray()
            .Select(e => new Entry(
                e.GetProperty("name").GetString()!,
                e.GetProperty("status").GetString()!,
                e.GetProperty("markdown").GetString()!,
                e.GetProperty("write").GetString()!,
                e.GetProperty("html").GetString()!))
            .ToList();
    }

    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (var entry in Load())
        {
            data.Add(entry.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void WriteVerdict_ShouldMatchTheFixture(string name)
    {
        var entry = Load().Single(e => e.Name == name);
        var accepted = ContentMarkdownRules.Check(entry.Markdown).Count == 0;
        Assert.Equal(entry.Write == "accept", accepted);
    }

    [Fact]
    public void Fixture_ShouldBeWellFormed()
    {
        var entries = Load();
        Assert.Equal(29, entries.Count);
        Assert.Equal(entries.Count, entries.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(entries, e => Assert.Contains(e.Status, new[] { "candidate", "confirmed" }));
        Assert.All(entries, e => Assert.Contains(e.Write, new[] { "accept", "reject" }));
    }
}
