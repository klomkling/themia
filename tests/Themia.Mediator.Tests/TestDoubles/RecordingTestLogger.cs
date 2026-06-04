using Microsoft.Extensions.Logging;

namespace Themia.Mediator.Tests.TestDoubles;

/// <summary>
/// <see cref="ILogger{T}"/> test double that records every log entry for assertion.
/// </summary>
internal sealed class RecordingTestLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    /// <summary>All log entries recorded in call order.</summary>
    public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _entries.Add((logLevel, formatter(state, exception)));
    }
}
