using Microsoft.Extensions.Logging;

namespace Themia.Messaging.AspNetCore.Tests;

/// <summary>An <see cref="ILogger{TCategoryName}"/> that records every log entry (level + formatted message) for assertion.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception)));
}

/// <summary>An <see cref="ILoggerProvider"/> that records every log entry across all categories, for host-level tests.</summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    public List<(string Category, LogLevel Level, string Message)> Entries { get; } = [];

    public ILogger CreateLogger(string categoryName) => new CategoryLogger(categoryName, Entries);

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
