using Quartz;
using Xunit;

namespace Themia.Quartz.Tests;

/// <summary>
/// Job, trigger and group names are user-supplied; every dashboard view must HTML-encode them. Handlebars.Net
/// suppresses encoding for a view that declares a layout, so ViewEngine applies the layout itself.
/// </summary>
public sealed class DashboardEncodingTests
{
    private const string HostileName = "evil</script><img src=x onerror=alert(1)>\"'&";
    private const string HostileGroup = "grp\"&<b>";
    private const string RawMarkup = "<img src=x";
    private const string RawScriptClose = "</script><img";

    public sealed class NoopJob : IJob
    {
        public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
    }

    private static string Q(string value) => Uri.EscapeDataString(value);

    public static TheoryData<string> Views() => new()
    {
        "/jobs/Jobs/Index",
        $"/jobs/Jobs/Edit?name={Q(HostileName)}&group={Q(HostileGroup)}",
        $"/jobs/Jobs/Trigger?name={Q(HostileName)}&group={Q(HostileGroup)}",
        "/jobs/Triggers/Index",
        $"/jobs/Triggers/Edit?name={Q(HostileName)}&group={Q(HostileGroup)}",
    };

    [Theory]
    [MemberData(nameof(Views))]
    public async Task View_ShouldEncodeNames_WhenJobAndTriggerNamesHoldMarkup(string url)
    {
        await using var scope = await DashboardSmokeTests.StartHostAsync(authorize: _ => Task.FromResult(true));
        var job = JobBuilder.Create<NoopJob>().WithIdentity(HostileName, HostileGroup).StoreDurably().Build();
        await scope.Scheduler.AddJob(job, replace: true);
        await scope.Scheduler.ScheduleJob(TriggerBuilder.Create().WithIdentity(HostileName, HostileGroup)
            .ForJob(job).WithCronSchedule("0 0 0 * * ?").Build());

        var response = await scope.CreateClient().GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}");
        Assert.DoesNotContain(RawMarkup, body, StringComparison.Ordinal);
        Assert.DoesNotContain(RawScriptClose, body, StringComparison.Ordinal);
    }
}
