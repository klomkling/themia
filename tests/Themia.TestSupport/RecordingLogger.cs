using Microsoft.Extensions.Logging;

namespace Themia.TestSupport;

/// <summary>
/// An <see cref="ILogger{TCategoryName}"/> that records every entry (level and formatted message) so a
/// test can assert on what was logged.
/// </summary>
/// <remarks>
/// Shared because this class had been hand-copied into several test projects, member for member. A
/// change to how entries are captured — recording the <see cref="EventId"/> or scopes, say — had to be
/// made in every copy, and a reviewer comparing two test projects could not tell whether their fakes
/// behaved identically. One definition, one place to change.
/// </remarks>
/// <typeparam name="T">The logger category type.</typeparam>
public sealed class RecordingLogger<T> : ILogger<T>
{
    /// <summary>Every entry recorded, in call order.</summary>
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    /// <summary>Just the formatted messages, for assertions that do not care about the level.</summary>
    public IEnumerable<string> Messages => Entries.Select(e => e.Message);

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception)));
}

/// <summary>
/// An <see cref="ILoggerProvider"/> that records entries across every category, for host-level tests
/// where the logger under assertion is resolved by the framework rather than injected.
/// </summary>
public sealed class RecordingLoggerProvider : ILoggerProvider
{
    /// <summary>Every entry recorded, in call order, with its category.</summary>
    public List<(string Category, LogLevel Level, string Message)> Entries { get; } = [];

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new CategoryLogger(categoryName, Entries);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private sealed class CategoryLogger(string category, List<(string Category, LogLevel Level, string Message)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => entries.Add((category, logLevel, formatter(state, exception)));
    }
}
