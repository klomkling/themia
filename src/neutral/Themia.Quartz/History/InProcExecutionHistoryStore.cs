namespace Themia.Quartz;

/// <summary>
/// In-process, in-memory implementation of <see cref="IExecutionHistoryStore"/>.
/// Data is lost when the process restarts. For persistent history, use the EF-backed
/// store provided by <c>Themia.Modules.Scheduling</c>.
/// </summary>
/// <remarks>
/// Vendored from SilkierQuartz (https://github.com/maikebing/SilkierQuartz,
/// commit 4b974e080d369c588194e84642a9be875175f3fd) under the MIT licence.
/// Re-namespaced from <c>Quartz.Plugins.RecentHistory.Impl</c> to <c>Themia.Quartz</c>.
/// </remarks>
[Serializable]
public class InProcExecutionHistoryStore : IExecutionHistoryStore
{
    /// <inheritdoc/>
    public string SchedulerName { get; set; } = string.Empty;

    private readonly Dictionary<string, ExecutionHistoryEntry> _data = new();
    private DateTime _nextPurgeTime = DateTime.UtcNow;
    private int _updatesFromLastPurge;
    private int _totalJobsExecuted;
    private int _totalJobsFailed;

    /// <inheritdoc/>
    public Task<ExecutionHistoryEntry?> Get(string fireInstanceId)
    {
        lock (_data)
        {
            if (_data.TryGetValue(fireInstanceId, out var entry))
                return Task.FromResult<ExecutionHistoryEntry?>(entry);
            return Task.FromResult<ExecutionHistoryEntry?>(null);
        }
    }

    /// <inheritdoc/>
    public async Task Purge()
    {
        var ids = new HashSet<string>((await FilterLastOfEveryTrigger(10)).Select(x => x.FireInstanceId!));

        lock (_data)
        {
            foreach (var key in _data.Keys.ToArray())
            {
                if (!ids.Contains(key))
                    _data.Remove(key);
            }
        }
    }

    /// <inheritdoc/>
    public async Task Save(ExecutionHistoryEntry entry)
    {
        _updatesFromLastPurge++;

        if (_updatesFromLastPurge >= 10 || _nextPurgeTime < DateTime.UtcNow)
        {
            _nextPurgeTime = DateTime.UtcNow.AddMinutes(1);
            _updatesFromLastPurge = 0;
            await Purge();
        }

        lock (_data)
        {
            _data[entry.FireInstanceId!] = entry;
        }
    }

    /// <inheritdoc/>
    public Task<IEnumerable<ExecutionHistoryEntry>> FilterLastOfEveryJob(int limitPerJob)
    {
        lock (_data)
        {
            IEnumerable<ExecutionHistoryEntry> result = _data.Values
                .Where(x => x.SchedulerName == SchedulerName)
                .GroupBy(x => x.Job)
                .Select(x => x.OrderByDescending(y => y.ActualFireTimeUtc).Take(limitPerJob).Reverse())
                .SelectMany(x => x).ToArray();
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc/>
    public Task<IEnumerable<ExecutionHistoryEntry>> FilterLastOfEveryTrigger(int limitPerTrigger)
    {
        lock (_data)
        {
            IEnumerable<ExecutionHistoryEntry> result = _data.Values
                .Where(x => x.SchedulerName == SchedulerName)
                .GroupBy(x => x.Trigger)
                .Select(x => x.OrderByDescending(y => y.ActualFireTimeUtc).Take(limitPerTrigger).Reverse())
                .SelectMany(x => x).ToArray();
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc/>
    public Task<IEnumerable<ExecutionHistoryEntry>> FilterLast(int limit)
    {
        lock (_data)
        {
            IEnumerable<ExecutionHistoryEntry> result = _data.Values
                .Where(x => x.SchedulerName == SchedulerName)
                .OrderByDescending(y => y.ActualFireTimeUtc).Take(limit).Reverse().ToArray();
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc/>
    public Task<int> GetTotalJobsExecuted() => Task.FromResult(_totalJobsExecuted);

    /// <inheritdoc/>
    public Task<int> GetTotalJobsFailed() => Task.FromResult(_totalJobsFailed);

    /// <inheritdoc/>
    public Task IncrementTotalJobsExecuted()
    {
        Interlocked.Increment(ref _totalJobsExecuted);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task IncrementTotalJobsFailed()
    {
        Interlocked.Increment(ref _totalJobsFailed);
        return Task.CompletedTask;
    }
}
