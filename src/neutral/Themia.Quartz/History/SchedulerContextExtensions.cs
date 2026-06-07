namespace Themia.Quartz;

/// <summary>Stores/retrieves the <see cref="IExecutionHistoryStore"/> on a Quartz scheduler context.</summary>
/// <remarks>
/// Vendored from SilkierQuartz (https://github.com/maikebing/SilkierQuartz,
/// commit 4b974e080d369c588194e84642a9be875175f3fd) under the MIT licence.
/// Re-namespaced from <c>Quartz.Plugins.RecentHistory</c> to <c>Themia.Quartz</c>.
/// The context key is a stable constant rather than <c>typeof(...).FullName</c> so that
/// re-namespacing cannot silently shift the key and break existing schedulers.
/// </remarks>
public static class SchedulerContextExtensions
{
    private const string Key = "Themia.Quartz.IExecutionHistoryStore";

    /// <summary>Associates the execution-history store with the scheduler context.</summary>
    public static void SetExecutionHistoryStore(this global::Quartz.SchedulerContext context, IExecutionHistoryStore store)
        => context[Key] = store;

    /// <summary>Gets the execution-history store from the scheduler context, or <see langword="null"/> if unset.</summary>
    public static IExecutionHistoryStore? GetExecutionHistoryStore(this global::Quartz.SchedulerContext context)
        => context.TryGetValue(Key, out var v) ? v as IExecutionHistoryStore : null;
}
