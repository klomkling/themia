using Microsoft.Extensions.Logging;

namespace Themia.Mediator.Tests.TestDoubles;

/// <summary>
/// No-op <see cref="ILogger{T}"/> for unit tests. No assertions are made on log output.
/// </summary>
internal sealed class NullTestLogger<T> : ILogger<T>
{
    public static readonly NullTestLogger<T> Instance = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => false;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
}
