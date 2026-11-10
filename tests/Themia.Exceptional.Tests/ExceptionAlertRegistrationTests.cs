using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Themia.Exceptional.Alerts;
using Xunit;

namespace Themia.Exceptional.Tests;

public class ExceptionAlertRegistrationTests
{
    private static void Enable(ExceptionAlertOptions options)
    {
        options.Enabled = true;
        options.BotToken = "123456:secret-token-value";
        options.ChatId = "-1001";
        options.AdminUrlTemplate = "https://admin.example.test/exceptions/{guid}";
    }

    [Fact]
    public void AddThemiaExceptionalAlerts_RegistersNothing_WhenDisabled()
    {
        var services = new ServiceCollection();

        services.AddThemiaExceptionalAlerts(_ => { });

        Assert.Empty(services);
    }

    [Fact]
    public void AddThemiaExceptionalAlerts_RegistersTheJobAndChannel_WhenEnabled()
    {
        var services = new ServiceCollection();

        services.AddThemiaExceptionalAlerts(Enable);

        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService));
        Assert.Contains(services, d => d.ServiceType == typeof(IAlertChannel));
    }

    [Theory]
    [InlineData("BotToken")]
    [InlineData("ChatId")]
    [InlineData("AdminUrlTemplate")]
    public void AddThemiaExceptionalAlerts_Throws_WhenEnabledWithoutARequiredSetting(string missing)
    {
        void Configure(ExceptionAlertOptions options)
        {
            Enable(options);
            typeof(ExceptionAlertOptions).GetProperty(missing)!.SetValue(options, string.Empty);
        }

        var ex = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddThemiaExceptionalAlerts(Configure));

        Assert.Contains(missing, ex.Message);
        Assert.DoesNotContain("secret-token-value", ex.Message);
    }

    [Fact]
    public void Validate_Throws_WhenTheLeaseIsShorterThanTheLongestSend()
    {
        var options = new ExceptionAlertOptions { Lease = TimeSpan.FromSeconds(30) };
        Enable(options);

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("Lease", ex.Message);
    }

    [Fact]
    public void Validate_AcceptsTheDefaults_WhenEnabled()
    {
        var options = new ExceptionAlertOptions();
        Enable(options);

        options.Validate();
    }

    [Fact]
    public void Validate_IgnoresEverything_WhenDisabled()
    {
        new ExceptionAlertOptions { BotToken = string.Empty, Lease = TimeSpan.Zero }.Validate();
    }

    [Fact]
    public void ResolvingTheJob_Fails_WhenTheDialectHasNoAlertSupport()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new ExceptionalOptions { ApplicationName = "App" });
        services.AddSingleton(DispatchProxy.Create<IExceptionalSqlDialect, NoAlertDialectProxy>());
        services.AddThemiaExceptionalAlerts(Enable);
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetServices<IHostedService>().ToList());

        Assert.Contains(nameof(IExceptionAlertSqlDialect), ex.Message);
    }

    public class NoAlertDialectProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new NotSupportedException();
    }
}
