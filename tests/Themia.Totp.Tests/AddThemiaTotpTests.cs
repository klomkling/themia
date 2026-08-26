using Microsoft.Extensions.DependencyInjection;
using Themia.Totp;
using Xunit;

namespace Themia.Totp.Tests;

/// <summary>
/// The registration must make the replay store impossible to forget. There is deliberately no
/// parameterless overload and no default in-memory store — see <see cref="ITotpReplayStore"/>.
/// </summary>
public sealed class AddThemiaTotpTests
{
    private sealed class NoopStore : ITotpReplayStore
    {
        public ValueTask<bool> TryConsumeAsync(string secretId, long matchedStep, CancellationToken ct = default)
            => ValueTask.FromResult(true);
    }

    private sealed class OtherStore : ITotpReplayStore
    {
        public ValueTask<bool> TryConsumeAsync(string secretId, long matchedStep, CancellationToken ct = default)
            => ValueTask.FromResult(true);
    }

    [Fact]
    public void Registers_the_service_and_the_named_store()
    {
        var services = new ServiceCollection();
        services.AddThemiaTotp<NoopStore>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsType<TotpService>(scope.ServiceProvider.GetRequiredService<ITotpService>());
        Assert.IsType<NoopStore>(scope.ServiceProvider.GetRequiredService<ITotpReplayStore>());
    }

    [Fact]
    public void A_store_the_caller_registered_first_wins()
    {
        // TryAdd: a caller who needs a different lifetime, or a factory, registers it themselves and
        // this must not overwrite them.
        var services = new ServiceCollection();
        services.AddSingleton<ITotpReplayStore, OtherStore>();
        services.AddThemiaTotp<NoopStore>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsType<OtherStore>(scope.ServiceProvider.GetRequiredService<ITotpReplayStore>());
    }

    [Fact]
    public void Options_are_applied()
    {
        var services = new ServiceCollection();
        services.AddThemiaTotp<NoopStore>(o =>
        {
            o.Digits = 8;
            o.VerificationWindowSteps = 0;
        });

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITotpService>();

        Assert.Equal(8, service.GenerateCode("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ").Length);
    }

    [Fact]
    public void Defaults_to_the_system_clock_when_the_host_registers_none()
    {
        var services = new ServiceCollection();
        services.AddThemiaTotp<NoopStore>();

        using var provider = services.BuildServiceProvider();

        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
    }
}
