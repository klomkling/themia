namespace Themia.Challenges.IntegrationTests;

/// <summary>
/// Awaits every racer of a forced-overlap test and, when any of them failed, reports what each one did. A bare
/// <c>await Task.WhenAll</c> rethrows only the first exception, so a racer that failed for a different reason than the
/// one reported is invisible, and a rendezvous timeout in one racer hides why its partner never arrived.
/// </summary>
internal static class RaceOutcome
{
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
