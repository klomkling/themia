using Quartz;
using Quartz.Impl.Calendar;
using Xunit;

namespace Themia.Quartz.Tests;

/// <summary>
/// Follow-up to <see cref="DashboardEncodingTests"/>: the remaining list views (History, Executions, Calendars
/// Index, Scheduler), the quote case in a generated action URL, and the clear error for a layout view rendered
/// without a page model.
/// </summary>
public sealed class DashboardEncodingFollowupTests
{
    private const string HostileName = "evil</script><img src=x onerror=alert(1)>\"'&";
    private const string HostileGroup = "grp\"&<b>";
    private const string RawMarkup = "<img src=x";
    private const string RawScriptClose = "</script><img";
    private const string QuotedName = "a\"b'c";

    public sealed class BlockingJob : IJob
    {
        public static readonly SemaphoreSlim Started = new(0);
        public static readonly SemaphoreSlim Release = new(0);

        public async Task Execute(IJobExecutionContext context)
        {
            Started.Release();
            await Release.WaitAsync();
        }
    }

    private static string Q(string value) => Uri.EscapeDataString(value);

    private static async Task<string> GetAsync(DashboardSmokeTests.TestHostScope scope, string url)
    {
        var response = await scope.CreateClient().GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}");
        return body;
    }

    private static void AssertNoRawMarkup(string body)
    {
        Assert.DoesNotContain(RawMarkup, body, StringComparison.Ordinal);
        Assert.DoesNotContain(RawScriptClose, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HistoryIndex_ShouldEncodeNames_WhenHistoryHoldsHostileNames()
    {
        await using var scope = await DashboardSmokeTests.StartHostAsync(authorize: _ => Task.FromResult(true));
        var store = new InProcExecutionHistoryStore { SchedulerName = scope.Scheduler.SchedulerName };
        scope.Scheduler.Context.SetExecutionHistoryStore(store);
        await store.Save(new ExecutionHistoryEntry
        {
            FireInstanceId = "fire-1",
            SchedulerName = scope.Scheduler.SchedulerName,
            SchedulerInstanceId = scope.Scheduler.SchedulerInstanceId,
            Job = $"{HostileGroup}.{HostileName}",
            Trigger = $"{HostileGroup}.{HostileName}",
            ScheduledFireTimeUtc = DateTime.UtcNow,
            ActualFireTimeUtc = DateTime.UtcNow,
            ExceptionMessage = HostileName,
        });

        var body = await GetAsync(scope, "/jobs/History/Index");

        AssertNoRawMarkup(body);
        Assert.Contains("evil&lt;/script&gt;&lt;img src=x onerror=alert(1)&gt;", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecutionsIndex_ShouldEncodeNames_WhenARunningJobHasHostileNames()
    {
        await using var scope = await DashboardSmokeTests.StartHostAsync(authorize: _ => Task.FromResult(true));
        var job = JobBuilder.Create<BlockingJob>().WithIdentity(HostileName, HostileGroup).StoreDurably().Build();
        await scope.Scheduler.AddJob(job, replace: true);
        await scope.Scheduler.TriggerJob(job.Key);
        try
        {
            Assert.True(await BlockingJob.Started.WaitAsync(TimeSpan.FromSeconds(10)));
            var body = await GetAsync(scope, "/jobs/Executions/Index");

            AssertNoRawMarkup(body);
            Assert.Contains("evil&lt;/script&gt;&lt;img src=x onerror=alert(1)&gt;", body, StringComparison.Ordinal);
        }
        finally
        {
            BlockingJob.Release.Release();
        }
    }

    [Fact]
    public async Task CalendarsIndex_ShouldEncodeNames_WhenCalendarNameHoldsMarkup()
    {
        await using var scope = await DashboardSmokeTests.StartHostAsync(authorize: _ => Task.FromResult(true));
        await scope.Scheduler.AddCalendar(HostileName, new HolidayCalendar(), replace: true, updateTriggers: false);

        var body = await GetAsync(scope, "/jobs/Calendars/Index");

        AssertNoRawMarkup(body);
        Assert.Contains("evil&lt;/script&gt;&lt;img src=x onerror=alert(1)&gt;", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SchedulerIndex_ShouldEncodeGroupNames_WhenGroupsHoldMarkup()
    {
        await using var scope = await DashboardSmokeTests.StartHostAsync(authorize: _ => Task.FromResult(true));
        var job = JobBuilder.Create<DashboardEncodingTests.NoopJob>().WithIdentity("job", HostileName).StoreDurably().Build();
        await scope.Scheduler.AddJob(job, replace: true);

        var body = await GetAsync(scope, "/jobs/Scheduler/Index");

        AssertNoRawMarkup(body);
        Assert.Contains("evil&lt;/script&gt;&lt;img src=x onerror=alert(1)&gt;", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JobTrigger_ShouldKeepQuotesInsideTheAttribute_WhenNameHoldsQuotes()
    {
        await using var scope = await DashboardSmokeTests.StartHostAsync(authorize: _ => Task.FromResult(true));
        var job = JobBuilder.Create<DashboardEncodingTests.NoopJob>().WithIdentity(QuotedName, QuotedName).StoreDurably().Build();
        await scope.Scheduler.AddJob(job, replace: true);

        var body = await GetAsync(scope, $"/jobs/Jobs/Trigger?name={Q(QuotedName)}&group={Q(QuotedName)}");

        Assert.DoesNotContain($"name={QuotedName}", body, StringComparison.Ordinal);
        Assert.DoesNotContain("name=a\"b", body, StringComparison.Ordinal);
        Assert.Contains("a&quot;b'c.a&quot;b'c", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ShouldThrowClearError_WhenLayoutViewGetsNoPageModel()
    {
        var services = Dashboard.Services.Create(new ThemiaQuartzOptions());

        var ex = Assert.Throws<InvalidOperationException>(() => services.ViewEngine.Render("Jobs/Index.hbs", new object()));

        Assert.Contains("PageControllerBase.Page", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Jobs/Index.hbs", ex.Message, StringComparison.Ordinal);
    }
}
