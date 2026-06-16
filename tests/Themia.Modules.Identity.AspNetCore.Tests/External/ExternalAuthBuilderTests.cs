using Microsoft.Extensions.DependencyInjection;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.AspNetCore.DependencyInjection;
using Themia.Modules.Identity.AspNetCore.External;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.Tests.External;

public sealed class ExternalAuthBuilderTests
{
    private static ServiceProvider Build(Action<ExternalAuthBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddThemiaExternalAuth();
        configure(builder);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddGoogle_registers_a_provider_resolvable_by_name()
    {
        using var provider = Build(b => b.AddGoogle(o =>
        {
            o.ClientId = "google-client";
            o.ClientSecret = "google-secret";
        }));

        var registry = provider.GetRequiredService<IExternalAuthProviderRegistry>();
        Assert.True(registry.TryGet("google", out var google));
        Assert.Equal("google", google!.Name);
    }

    [Fact]
    public void AddLine_registers_a_provider_resolvable_by_name_case_insensitively()
    {
        using var provider = Build(b => b.AddLine(o =>
        {
            o.ChannelId = "line-channel";
            o.ChannelSecret = "line-channel-secret-32-bytes-long!";
        }));

        var registry = provider.GetRequiredService<IExternalAuthProviderRegistry>();
        Assert.True(registry.TryGet("LINE", out var line));
        Assert.Equal("line", line!.Name);
    }

    [Fact]
    public void AddGoogle_and_AddLine_register_both_providers()
    {
        using var provider = Build(b => b
            .AddGoogle(o => { o.ClientId = "gc"; o.ClientSecret = "gs"; })
            .AddLine(o => { o.ChannelId = "lc"; o.ChannelSecret = "ls-secret-32-bytes-minimum-padding"; }));

        var registry = provider.GetRequiredService<IExternalAuthProviderRegistry>();
        Assert.True(registry.TryGet("google", out _));
        Assert.True(registry.TryGet("line", out _));
        Assert.False(registry.TryGet("unknown", out _));
    }

    [Fact]
    public void AddGoogle_with_blank_client_id_throws_on_build()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Build(b => b.AddGoogle(o => { o.ClientId = ""; o.ClientSecret = "secret"; })));
        Assert.Contains("ClientId", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddLine_with_blank_secret_throws_on_build()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Build(b => b.AddLine(o => { o.ChannelId = "channel"; o.ChannelSecret = " "; })));
        Assert.Contains("ChannelSecret", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddProvider_instance_is_resolvable_by_name()
    {
        using var provider = Build(b => b.AddProvider(new FakeProvider("custom")));

        var registry = provider.GetRequiredService<IExternalAuthProviderRegistry>();
        Assert.True(registry.TryGet("custom", out var custom));
        Assert.IsType<FakeProvider>(custom);
    }

    private sealed class FakeProvider(string name) : IExternalAuthProvider
    {
        public string Name { get; } = name;

        public Task<ExternalAuthResult> ExchangeAsync(
            ExternalAuthRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(ExternalAuthResult.Failed("not-implemented"));
    }
}
