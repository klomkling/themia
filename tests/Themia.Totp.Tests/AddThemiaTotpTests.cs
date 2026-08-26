using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
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
        public ValueTask<bool> TryAdvanceAsync(string secretId, long matchedStep, CancellationToken ct = default)
            => ValueTask.FromResult(true);
    }

    private sealed class OtherStore : ITotpReplayStore
    {
        public ValueTask<bool> TryAdvanceAsync(string secretId, long matchedStep, CancellationToken ct = default)
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
    public async Task Bad_options_fail_the_host_at_startup_and_name_the_value()
    {
        // Without ValidateOnStart this surfaces as an exception from DI resolution on the first login,
        // because TotpService is scoped — a configuration error reported as a runtime failure, by
        // whichever user happened to sign in first.
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddThemiaTotp<NoopStore>(o => o.Period = TimeSpan.FromMilliseconds(500));

        using var host = builder.Build();

        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains("Period", string.Join(" ", error.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Valid_options_start_the_host()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddThemiaTotp<NoopStore>(o => o.Digits = 8);

        using var host = builder.Build();

        await host.StartAsync();
        await host.StopAsync();
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
