using Serilog;
using Themia.Exceptional;
using Themia.Exceptional.Serilog;
using Xunit;

namespace Themia.Exceptional.Tests;

public sealed class ExceptionalSerilogSinkContextTests
{
    private sealed class CapturingStore : IExceptionStore
    {
        public ExceptionEntry? Last;
        public Task LogAsync(ExceptionEntry e, CancellationToken ct = default) { Last = e; return Task.CompletedTask; }
        public Task<ExceptionEntry?> GetAsync(Guid g, CancellationToken ct = default) => Task.FromResult<ExceptionEntry?>(null);
        public Task<PagedResult<ExceptionEntry>> ListAsync(ExceptionFilter f, CancellationToken ct = default) => Task.FromResult(new PagedResult<ExceptionEntry>());
        public Task<int> CountAsync(ExceptionFilter f, CancellationToken ct = default) => Task.FromResult(0);
        public Task<bool> ProtectAsync(Guid g, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> DeleteAsync(Guid g, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> HardDeleteAsync(Guid g, CancellationToken ct = default) => Task.FromResult(true);
        public Task<int> PurgeAsync(DateTime o, CancellationToken ct = default) => Task.FromResult(0);
    }

    [Fact]
    public void Emit_MapsRequestContextProperty()
    {
        var store = new CapturingStore();
        var options = new ExceptionalOptions { ApplicationName = "t" };
        var logger = new LoggerConfiguration()
            .Enrich.WithProperty("RequestContext", "{\"headers\":{\"A\":\"1\"}}")
            .WriteTo.Sink(new ExceptionalSerilogSink(store, options))
            .CreateLogger();

        logger.Error(new InvalidOperationException("boom"), "err");

        Assert.Equal("{\"headers\":{\"A\":\"1\"}}", store.Last!.RequestContext);
    }
}
