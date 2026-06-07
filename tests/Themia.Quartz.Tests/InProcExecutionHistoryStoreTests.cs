using Themia.Quartz;
using Xunit;

namespace Themia.Quartz.Tests;

public class InProcExecutionHistoryStoreTests
{
    private static ExecutionHistoryEntry Entry(string fireId, string job, string trigger, DateTimeOffset fired) => new()
    {
        FireInstanceId = fireId, SchedulerName = "S", SchedulerInstanceId = "I",
        Job = job, Trigger = trigger, ActualFireTimeUtc = fired,
    };

    [Fact]
    public async Task Save_Then_Get_RoundTrips()
    {
        var store = new InProcExecutionHistoryStore { SchedulerName = "S" };
        await store.Save(Entry("f1", "g.j", "g.t", DateTimeOffset.UtcNow));
        Assert.Equal("g.j", (await store.Get("f1"))!.Job);
    }

    [Fact]
    public async Task FilterLast_ReturnsMostRecentForScheduler()
    {
        var store = new InProcExecutionHistoryStore { SchedulerName = "S" };
        await store.Save(Entry("f1", "g.j", "g.t", DateTimeOffset.UtcNow.AddMinutes(-2)));
        await store.Save(Entry("f2", "g.j", "g.t", DateTimeOffset.UtcNow));
        var last = (await store.FilterLast(1)).ToList();
        Assert.Single(last);
        Assert.Equal("f2", last[0].FireInstanceId);
    }

    [Fact]
    public async Task IncrementCounters_Tracked()
    {
        var store = new InProcExecutionHistoryStore { SchedulerName = "S" };
        await store.IncrementTotalJobsExecuted();
        await store.IncrementTotalJobsExecuted();
        await store.IncrementTotalJobsFailed();
        Assert.Equal(2, await store.GetTotalJobsExecuted());
        Assert.Equal(1, await store.GetTotalJobsFailed());
    }

    [Fact]
    public async Task Save_NullEntry_Throws()
    {
        var store = new InProcExecutionHistoryStore { SchedulerName = "S" };
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.Save(null!));
    }

    [Fact]
    public async Task Save_NullOrEmptyFireInstanceId_Throws()
    {
        var store = new InProcExecutionHistoryStore { SchedulerName = "S" };
        var entry = new ExecutionHistoryEntry
        {
            FireInstanceId = "",
            SchedulerName = "S",
            SchedulerInstanceId = "I",
            Job = "g.j",
            Trigger = "g.t",
            ActualFireTimeUtc = DateTimeOffset.UtcNow,
        };
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.Save(entry));
    }
}
