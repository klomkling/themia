using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Themia.Data.Migrations.Tests;

/// <summary>
/// Pins the precedence rule between a failure in the migration body and a failure while tearing the
/// runner down.
/// </summary>
/// <remarks>
/// <para>
/// This is not a hypothetical. <c>ThemiaMigrations.Run</c> used <c>using var</c> for the runner's scope
/// and provider, and FluentMigrator's SQL Server processor disposes by calling
/// <c>RollbackTransaction()</c>. When a migration lost a deadlock or timed out, the transaction was
/// already dead, the rollback threw <c>InvalidOperationException("This SqlTransaction has completed")</c>
/// from <c>Dispose</c> — and C# lets a <c>using</c> variable's dispose exception <b>replace</b> the one
/// already in flight.
/// </para>
/// <para>
/// Two things broke because of it, and both were invisible. The operator saw a zombied-transaction
/// message instead of the deadlock or permission error that caused it, discarding the wrap written for
/// exactly that case. And every caller retrying on SQL error numbers stopped matching, because what
/// reached their <c>catch</c> was no longer a <c>SqlException</c> — which is why two earlier attempts at
/// the Scheduling integration flake (matching on the word "deadlock", then on error codes, then raising
/// the command timeout) all failed to close it: each targeted an exception that never arrived.
/// </para>
/// </remarks>
public class MigrationDisposePrecedenceTests
{
    private sealed class ThrowingScope : IServiceScope
    {
        public IServiceProvider ServiceProvider => throw new NotSupportedException();

        public void Dispose() => throw new InvalidOperationException("This SqlTransaction has completed; it is no longer usable.");
    }

    private sealed class ThrowingDisposable : IDisposable
    {
        public void Dispose() => throw new InvalidOperationException("provider dispose failed");
    }

    private sealed class NoOpScope : IServiceScope
    {
        public IServiceProvider ServiceProvider => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }

    [Fact]
    public void A_dispose_failure_never_replaces_the_error_the_body_already_reported()
    {
        // The whole defect in one line: with `using`, this threw the rollback error and the real cause
        // was gone.
        ThemiaMigrations.DisposeQuietly(new ThrowingScope(), new ThrowingDisposable(), bodyFaulted: true);
    }

    [Fact]
    public void A_dispose_failure_is_reported_when_nothing_else_was()
    {
        // Swallowing unconditionally would be the opposite defect: a runner that cannot tear down may be
        // holding a connection or a transaction open, and with the body green there is nothing else to
        // report it.
        var ex = Assert.Throws<InvalidOperationException>(
            () => ThemiaMigrations.DisposeQuietly(new ThrowingScope(), new ThrowingDisposable(), bodyFaulted: false));

        Assert.Contains("failed to dispose cleanly", ex.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public void The_scope_failure_wins_over_the_provider_failure()
    {
        // The scope disposes first and owns the processor, so its error is the one closer to the cause.
        var ex = Assert.Throws<InvalidOperationException>(
            () => ThemiaMigrations.DisposeQuietly(new ThrowingScope(), new ThrowingDisposable(), bodyFaulted: false));

        Assert.Contains("SqlTransaction", ex.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clean_teardown_throws_nothing()
    {
        ThemiaMigrations.DisposeQuietly(new NoOpScope(), new NoOpDisposable(), bodyFaulted: false);
        ThemiaMigrations.DisposeQuietly(new NoOpScope(), new NoOpDisposable(), bodyFaulted: true);
    }
}
