using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Themia.AspNetCore.Exceptions;
using Themia.Mediator.Abstractions;
using Themia.MultiTenancy;
using Themia.MultiTenancy.Abstractions;
using Themia.MultiTenancy.Mediator;
using Xunit;

namespace Themia.MultiTenancy.Mediator.Tests;

public class TenantGuardBehaviorTests
{
    private static readonly TenantInfo Tenant = new("acme", "acme");

    private static ClaimsPrincipal Authed(params string[] roles) =>
        new(new ClaimsIdentity(
            roles.Select(r => new Claim(ClaimTypes.Role, r)),
            authenticationType: "test", ClaimTypes.Name, ClaimTypes.Role));

    private static TenantGuardBehavior<TReq, string> Build<TReq>(
        ClaimsPrincipal? principal, TenantInfo? tenant, string[]? privileged, out CapturingLogger<TenantGuardBehavior<TReq, string>> logger)
        where TReq : IRequest<string>
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = principal is null ? null : new DefaultHttpContext { User = principal },
        };
        var options = Options.Create(new TenantGuardOptions { PrivilegedRoles = privileged ?? [] });
        logger = new CapturingLogger<TenantGuardBehavior<TReq, string>>();
        return new TenantGuardBehavior<TReq, string>(accessor, new FakeTenantAccessor(tenant), options, logger);
    }

    [Fact]
    public async Task Allow_InvokesNext_AndForwardsCancellationToken_WhenAuthedWithTenant()
    {
        var behavior = Build<TestRequest>(Authed("User"), Tenant, null, out _);
        using var cts = new CancellationTokenSource();
        var nextCalled = false;
        CancellationToken received = default;

        var result = await behavior.HandleAsync(
            new TestRequest(),
            ct => { nextCalled = true; received = ct; return Task.FromResult("ok"); },
            cts.Token);

        Assert.Equal("ok", result);
        Assert.True(nextCalled);
        Assert.Equal(cts.Token, received); // the caller's token is forwarded, not default
    }

    [Fact]
    public async Task Unauthenticated_Throws_WhenNoPrincipal()
    {
        var behavior = Build<TestRequest>(principal: null, tenant: Tenant, null, out var logger);

        await Assert.ThrowsAsync<UnauthorizedException>(() =>
            behavior.HandleAsync(new TestRequest(), _ => Task.FromResult("unused"), CancellationToken.None));

        Assert.Empty(logger.Entries); // warn only on NoTenant, never on Unauthenticated
    }

    [Fact]
    public async Task NoTenant_ThrowsForbidden_AndLogsWarning_WhenAuthedTenantless()
    {
        var behavior = Build<TestRequest>(Authed("User"), tenant: null, null, out var logger);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            behavior.HandleAsync(new TestRequest(), _ => Task.FromResult("unused"), CancellationToken.None));

        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        // Locks the no-PII contract: the message names the request type and carries no user identifier.
        Assert.Contains(nameof(TestRequest), warning.Message);
        Assert.DoesNotContain("UserId", warning.Message);
    }

    [Fact]
    public async Task Skip_InvokesNext_EvenWhenUnauthenticatedAndTenantless()
    {
        var behavior = Build<SkippableRequest>(principal: null, tenant: null, null, out _);

        var result = await behavior.HandleAsync(new SkippableRequest(), _ => Task.FromResult("ok"), CancellationToken.None);

        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task PrivilegedRole_InvokesNext_WithNullTenant()
    {
        var behavior = Build<TestRequest>(Authed("SaaSAdmin"), tenant: null, ["SaaSAdmin"], out _);

        var result = await behavior.HandleAsync(new TestRequest(), _ => Task.FromResult("ok"), CancellationToken.None);

        Assert.Equal("ok", result);
    }
}

internal sealed record TestRequest : IRequest<string>;

internal sealed record SkippableRequest : IRequest<string>, ISkipTenantValidation;

internal sealed class FakeTenantAccessor(TenantInfo? current) : ITenantAccessor
{
    public TenantInfo? Current { get; } = current;
}

internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
