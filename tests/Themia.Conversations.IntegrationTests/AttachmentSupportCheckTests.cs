using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Themia.Conversations.Internal;
using Themia.Imaging;
using Themia.Storage;
using Themia.Storage.Local;
using Xunit;

namespace Themia.Conversations.IntegrationTests;

public sealed class AttachmentSupportCheckTests
{
    [Fact]
    public async Task Start_ShouldWarn_WhenTheStorageIsTheLocalProvider()
    {
        var log = new CapturingLogger();
        var local = new LocalStorageProvider(new LocalStorageOptions { RootPath = Path.Combine(Path.GetTempPath(), "themia-conv-check") });

        await Check(local, log).StartAsync(CancellationToken.None);

        Assert.Contains(log.Warnings, w => w.Contains("cannot force a download", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Start_ShouldNotWarn_WhenTheStorageCanSignADisposition()
    {
        var log = new CapturingLogger();

        await Check(new ConversationAttachmentTests.MemoryStorage(), log).StartAsync(CancellationToken.None);

        Assert.Empty(log.Warnings);
    }

    private static AttachmentSupportCheck Check(IStorageProvider storage, ILogger<AttachmentSupportCheck> log)
    {
        var services = new ServiceCollection()
            .AddSingleton(storage)
            .AddSingleton<IImageProcessor>(new SkiaImageProcessor(Options.Create(new ImageProcessingOptions())))
            .BuildServiceProvider();
        return new AttachmentSupportCheck(services, log);
    }

    private sealed class CapturingLogger : ILogger<AttachmentSupportCheck>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
