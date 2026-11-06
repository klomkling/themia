namespace Themia.Challenges.IntegrationTests;

/// <summary>
/// Awaits every racer of a forced-overlap test and, when any of them failed, reports what each one did. A bare
/// <c>await Task.WhenAll</c> rethrows only the first exception, so a racer that failed for a different reason than the
/// one reported is invisible, and a rendezvous timeout in one racer hides why its partner never arrived.
/// </summary>
internal static class RaceOutcome
{
    /// <summary>
    /// Starts every racer on a thread-pool thread of its own, then awaits them as <see cref="WhenAllReportedAsync{T}(Task{T}[])"/>.
    /// </summary>
    /// <remarks>
    /// A racer must never be started inline on the test thread. A gated call can run all the way to its blocking
    /// rendezvous before its first incomplete await (an async database call that completes synchronously, such as a
    /// pooled connection answering from a hot buffer, does not yield), and then the caller never gets to start the
    /// partner: the first racer times out alone, then the second does, and the forced overlap is lost. Running each
    /// racer from <see cref="Task.Run(Func{Task})"/> also keeps its continuations off the test framework's
    /// synchronization context, so a blocked racer cannot starve its partner of a worker.
    /// </remarks>
    public static Task<T[]> WhenAllReportedAsync<T>(params Func<Task<T>>[] racers) =>
        WhenAllReportedAsync(racers.Select(racer => Task.Run(racer)).ToArray());

    /// <summary>The non-generic form of <see cref="WhenAllReportedAsync{T}(Func{Task{T}}[])"/>.</summary>
    public static Task WhenAllReportedAsync(params Func<Task>[] racers) =>
        WhenAllReportedAsync(racers.Select(racer => Task.Run(racer)).ToArray());

    /// <summary>Awaits all <paramref name="racers"/>; throws one exception that names each racer's end state when any failed.</summary>
    public static async Task<T[]> WhenAllReportedAsync<T>(params Task<T>[] racers)
    {
        try
        {
            return await Task.WhenAll(racers);
        }
        catch
        {
            throw new AggregateException(Describe(racers), racers.Where(r => r.IsFaulted).SelectMany(r => r.Exception!.InnerExceptions));
        }
    }

    /// <summary>The non-generic form, for racers that return nothing the test reads.</summary>
    public static async Task WhenAllReportedAsync(params Task[] racers)
    {
        try
        {
            await Task.WhenAll(racers);
        }
        catch
        {
            throw new AggregateException(Describe(racers), racers.Where(r => r.IsFaulted).SelectMany(r => r.Exception!.InnerExceptions));
        }
    }

    private static string Describe(IReadOnlyList<Task> racers)
    {
        var lines = racers.Select((r, i) => $"racer {i}: " + r.Status switch
        {
            TaskStatus.RanToCompletion => "completed" + (ResultOf(r) is { } result ? $" with {result}" : string.Empty),
            TaskStatus.Faulted => $"failed with {r.Exception!.GetBaseException().GetType().Name}: {r.Exception.GetBaseException().Message}",
            _ => r.Status.ToString(),
        });
        return "A racer of the forced overlap failed. " + string.Join("; ", lines);
    }

    private static object? ResultOf(Task task) =>
        task.GetType().IsGenericType ? task.GetType().GetProperty("Result")?.GetValue(task) : null;
}
