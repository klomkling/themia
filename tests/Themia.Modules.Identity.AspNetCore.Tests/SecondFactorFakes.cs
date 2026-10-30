using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.AspNetCore.Tests;

internal sealed class ScriptedSecondFactorPolicy : ISecondFactorPolicy
{
    public SecondFactorRequirement Requirement { get; set; } = SecondFactorRequirement.NotRequired;

    public bool Throw { get; set; }

    public int Calls { get; private set; }

    public List<SecondFactorContext> Contexts { get; } = [];

    public ValueTask<SecondFactorRequirement> EvaluateAsync(SecondFactorContext context, CancellationToken cancellationToken = default)
    {
        Calls++;
        Contexts.Add(context);
        return Throw
            ? throw new InvalidOperationException("policy failed")
            : ValueTask.FromResult(Requirement);
    }
}

internal sealed class RecordingExternalHooks : IExternalAuthenticationHooks
{
    public int Succeeded { get; private set; }

    public int Failed { get; private set; }

    public bool Deny { get; set; }

    public Task OnBeforeExternalLoginAsync(BeforeExternalLoginContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task OnExternalLoginSucceededAsync(ExternalLoginSucceededContext context, CancellationToken cancellationToken = default)
    {
        Succeeded++;
        if (Deny)
        {
            context.Deny("gated");
        }

        return Task.CompletedTask;
    }

    public Task OnExternalLoginFailedAsync(ExternalLoginFailedContext context, CancellationToken cancellationToken = default)
    {
        Failed++;
        return Task.CompletedTask;
    }
}

/// <summary>A single-use, expiring token store like the real one: the raw value is stored hashed and consumed once.</summary>
internal sealed class InMemoryUserTokenService(TimeProvider clock) : IUserTokenService
{
    private readonly Dictionary<(Guid UserId, TokenPurpose Purpose, string Hash), (DateTimeOffset ExpiresAt, bool Consumed)> store = [];

    public Task<string> GenerateAsync(Guid userId, TokenPurpose purpose, TimeSpan? lifetime = null, CancellationToken cancellationToken = default)
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        store[(userId, purpose, Hash(raw))] = (clock.GetUtcNow().Add(lifetime ?? TimeSpan.FromHours(1)), false);
        return Task.FromResult(raw);
    }

    public Task<TokenConsumeResult> ConsumeAsync(Guid userId, TokenPurpose purpose, string rawToken, CancellationToken cancellationToken = default)
    {
        var key = (userId, purpose, Hash(rawToken));
        if (!store.TryGetValue(key, out var entry))
        {
            return Task.FromResult(TokenConsumeResult.NotFound);
        }

        if (entry.Consumed)
        {
            return Task.FromResult(TokenConsumeResult.AlreadyConsumed);
        }

        if (entry.ExpiresAt <= clock.GetUtcNow())
        {
            return Task.FromResult(TokenConsumeResult.Expired);
        }

        store[key] = (entry.ExpiresAt, true);
        return Task.FromResult(TokenConsumeResult.Success);
    }

    private static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));
}

internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<string> lines = [];

    public string Text => string.Join('\n', lines);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        lines.Add(formatter(state, exception) + exception);
}
