using Themia.Modules.Notifications.Outbox;

using Xunit;

namespace Themia.Modules.Notifications.Tests.Outbox;

public class DrainSignalTests
{
    [Fact]
    public async Task WaitAsync_completes_after_Signal()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var signal = new DrainSignal();
        var wait = signal.WaitAsync(cts.Token);
        signal.Signal();
        await wait; // does not hang
    }

    [Fact]
    public async Task WaitAsync_completes_when_already_signaled()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var signal = new DrainSignal();
        signal.Signal();
        await signal.WaitAsync(cts.Token);
    }
}
