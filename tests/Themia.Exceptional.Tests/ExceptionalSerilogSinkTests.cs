using Serilog.Events;
using Serilog.Parsing;
using Themia.Exceptional;
using Themia.Exceptional.Serilog;
using Xunit;

namespace Themia.Exceptional.Tests;

public class ExceptionalSerilogSinkTests
{
    private sealed class CapturingStore : IExceptionStore
    {
        public List<ExceptionEntry> Logged { get; } = new();
        public bool Throw { get; set; }
        public Task LogAsync(ExceptionEntry entry, CancellationToken ct = default)
        {
            if (Throw) throw new InvalidOperationException("store down");
            Logged.Add(entry);
            return Task.CompletedTask;
        }
        public Task<ExceptionEntry?> GetAsync(Guid guid, CancellationToken ct = default) => Task.FromResult<ExceptionEntry?>(null);
        public Task<PagedResult<ExceptionEntry>> ListAsync(ExceptionFilter f, CancellationToken ct = default) => Task.FromResult(new PagedResult<ExceptionEntry>());
        public Task<int> CountAsync(ExceptionFilter f, CancellationToken ct = default) => Task.FromResult(0);
        public Task<bool> ProtectAsync(Guid guid, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> DeleteAsync(Guid guid, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> HardDeleteAsync(Guid guid, CancellationToken ct = default) => Task.FromResult(false);
        public Task<int> PurgeAsync(DateTime olderThanUtc, CancellationToken ct = default) => Task.FromResult(0);
    }

    private static LogEvent Event(LogEventLevel level, Exception? ex) => new(
        DateTimeOffset.UtcNow, level, ex,
        new MessageTemplate("m", Array.Empty<MessageTemplateToken>()), Array.Empty<LogEventProperty>());

    [Fact]
    public void Emit_StoresErrorWithException()
    {
        var store = new CapturingStore();
        var sink = new ExceptionalSerilogSink(store, new ExceptionalOptions { ApplicationName = "App" });

        sink.Emit(Event(LogEventLevel.Error, new InvalidOperationException("boom")));

        Assert.Single(store.Logged);
        Assert.Equal("App", store.Logged[0].ApplicationName);
    }

    [Theory]
    [InlineData(LogEventLevel.Information)]
    [InlineData(LogEventLevel.Warning)]
    public void Emit_Ignores_BelowError(LogEventLevel level)
    {
        var store = new CapturingStore();
        var sink = new ExceptionalSerilogSink(store, new ExceptionalOptions { ApplicationName = "App" });

        sink.Emit(Event(level, new InvalidOperationException("boom")));

        Assert.Empty(store.Logged);
    }

    [Fact]
    public void Emit_Ignores_ErrorWithoutException()
    {
        var store = new CapturingStore();
        var sink = new ExceptionalSerilogSink(store, new ExceptionalOptions { ApplicationName = "App" });

        sink.Emit(Event(LogEventLevel.Error, null));

        Assert.Empty(store.Logged);
    }

    [Fact]
    public void Emit_DoesNotThrow_WhenStoreFails()
    {
        var store = new CapturingStore { Throw = true };
        var sink = new ExceptionalSerilogSink(store, new ExceptionalOptions { ApplicationName = "App" });

        // Must not propagate — a logging failure can't break the app.
        sink.Emit(Event(LogEventLevel.Error, new InvalidOperationException("boom")));
    }
}
