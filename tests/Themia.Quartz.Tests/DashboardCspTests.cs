using System.Text.Json;
using System.Text.RegularExpressions;
using Quartz;
using Xunit;

namespace Themia.Quartz.Tests;

/// <summary>
/// The dashboard must run under <c>script-src 'self'; style-src 'self'</c> (coord #0236): no inline script
/// body, no <c>style=</c> attribute, no inline event handler, no <c>javascript:</c> URL in any rendered view,
/// and a hostile job / trigger / calendar name cannot break out of the JSON data block.
/// </summary>
public sealed class DashboardCspTests
{
    // A name that would break an inline script (</script>) or an attribute (quote, ampersand) if emitted raw.
    private const string HostileName = "evil</script><img src=x onerror=alert(1)>\"'&";
    private const string HostileGroup = "grp\"&<b>";

    private static readonly Regex InlineScriptBody = new(
        @"<script\b(?![^>]*\bsrc=)(?![^>]*type=""application/json"")[^>]*>\s*\S", RegexOptions.IgnoreCase);
    private static readonly Regex QuotedValue = new("\"[^\"]*\"");
    private static readonly Regex StyleAttribute = new(@"<[^<>]*\sstyle\s*=", RegexOptions.IgnoreCase);
    private static readonly Regex EventHandlerAttribute = new(@"<[^<>]*\son[a-z]+\s*=", RegexOptions.IgnoreCase);
    private static readonly Regex PageDataBlock = new(
        @"<script type=""application/json"" id=""page-data"">(.*?)</script>", RegexOptions.Singleline);

    public sealed class NoopJob : IJob
    {
        public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
    }

    private static async Task SeedAsync(IScheduler scheduler)
    {
        var job = JobBuilder.Create<NoopJob>().WithIdentity(HostileName, HostileGroup).StoreDurably().Build();
        await scheduler.AddJob(job, replace: true);
        var trigger = TriggerBuilder.Create().WithIdentity(HostileName, HostileGroup).ForJob(job)
            .WithCronSchedule("0 0 0 * * ?").Build();
        await scheduler.ScheduleJob(trigger);
    }

    private static string Q(string value) => Uri.EscapeDataString(value);

    public static TheoryData<string> Views() => new()
    {
        "/jobs/Scheduler/Index",
        "/jobs/Jobs/Index",
        "/jobs/Jobs/New",
        $"/jobs/Jobs/Edit?name={Q(HostileName)}&group={Q(HostileGroup)}",
        $"/jobs/Jobs/Trigger?name={Q(HostileName)}&group={Q(HostileGroup)}",
        "/jobs/Triggers/Index",
        "/jobs/Triggers/New",
        $"/jobs/Triggers/Edit?name={Q(HostileName)}&group={Q(HostileGroup)}",
        "/jobs/Calendars/Index",
        "/jobs/Calendars/New",
        "/jobs/Executions/Index",
        "/jobs/History/Index",
    };

    [Theory]
    [MemberData(nameof(Views))]
    public async Task View_ShouldRenderWithoutAnythingStrictCspBlocks(string url)
    {
        await using var scope = await DashboardSmokeTests.StartHostAsync(authorize: _ => Task.FromResult(true));
        await SeedAsync(scope.Scheduler);

        var response = await scope.CreateClient().GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}");
        Assert.DoesNotMatch(InlineScriptBody, body);
        // Attribute values are HTML-encoded (a hostile "onerror=" inside one is inert text), so check markup only.
        var markup = QuotedValue.Replace(body, "\"\"");
        Assert.DoesNotMatch(StyleAttribute, markup);
        Assert.DoesNotMatch(EventHandlerAttribute, markup);
        Assert.DoesNotContain("javascript:", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task JobEditPage_ShouldKeepHostileNamesInsideTheJsonBlock_WhenNameHoldsScriptClose()
    {
        await using var scope = await DashboardSmokeTests.StartHostAsync(authorize: _ => Task.FromResult(true));
        await SeedAsync(scope.Scheduler);

        var body = await (await scope.CreateClient()
            .GetAsync($"/jobs/Jobs/Edit?name={Q(HostileName)}&group={Q(HostileGroup)}")).Content.ReadAsStringAsync();

        var block = PageDataBlock.Match(body);
        Assert.True(block.Success, "page-data block missing");
        Assert.DoesNotContain("</", block.Groups[1].Value, StringComparison.Ordinal);
        Assert.DoesNotContain("<", block.Groups[1].Value, StringComparison.Ordinal);
        Assert.DoesNotContain(">", block.Groups[1].Value, StringComparison.Ordinal);
        Assert.DoesNotContain("&", block.Groups[1].Value, StringComparison.Ordinal);
        using var parsed = JsonDocument.Parse(block.Groups[1].Value);
        Assert.Equal(JsonValueKind.Object, parsed.RootElement.ValueKind);
        Assert.DoesNotContain("<img src=x", body, StringComparison.Ordinal);
    }
}
