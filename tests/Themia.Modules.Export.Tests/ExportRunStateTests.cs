using Themia.Modules.Export.Entities;
using Xunit;

namespace Themia.Modules.Export.Tests;

/// <summary>Unit tests for the <see cref="ExportRun"/> lifecycle state machine (no database).</summary>
public sealed class ExportRunStateTests
{
    private static ExportRun NewRun() =>
        new() { DefinitionKey = "k", Format = ExportFormat.Csv };

    [Fact]
    public void New_run_is_pending()
    {
        Assert.Equal(ExportRunStatus.Pending, NewRun().Status);
    }

    [Fact]
    public void MarkRunning_sets_status_and_started_at()
    {
        var run = NewRun();
        var started = DateTimeOffset.UtcNow;

        run.MarkRunning(started);

        Assert.Equal(ExportRunStatus.Running, run.Status);
        Assert.Equal(started, run.StartedAt);
    }

    [Fact]
    public void MarkFailed_clears_any_stored_result()
    {
        var run = NewRun();
        run.MarkSucceeded("exports/acme/x.csv", "x.csv", 42, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        run.MarkFailed("boom", DateTimeOffset.UtcNow);

        Assert.Null(run.StorageKey);
        Assert.Null(run.SizeBytes);
        Assert.Null(run.ExpiresAt);
    }

    [Fact]
    public void MarkSucceeded_sets_result_fields_and_clears_error()
    {
        var run = NewRun();
        var expires = DateTimeOffset.UtcNow.AddDays(7);
        var completed = DateTimeOffset.UtcNow;

        run.MarkSucceeded("exports/acme/x.csv", "x.csv", 42, expires, completed);

        Assert.Equal(ExportRunStatus.Succeeded, run.Status);
        Assert.Equal("exports/acme/x.csv", run.StorageKey);
        Assert.Equal("x.csv", run.FileName);
        Assert.Equal(42, run.SizeBytes);
        Assert.Equal(expires, run.ExpiresAt);
        Assert.Equal(completed, run.CompletedAt);
        Assert.Null(run.Error);
    }

    [Fact]
    public void MarkSucceeded_rejects_empty_storage_key()
    {
        Assert.Throws<ArgumentException>(() =>
            NewRun().MarkSucceeded("", "x.csv", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void MarkFailed_sets_error_and_completed_at()
    {
        var run = NewRun();
        var completed = DateTimeOffset.UtcNow;

        run.MarkFailed("boom", completed);

        Assert.Equal(ExportRunStatus.Failed, run.Status);
        Assert.Equal("boom", run.Error);
        Assert.Equal(completed, run.CompletedAt);
    }

    [Fact]
    public void MarkExpired_throws_when_run_is_not_succeeded()
    {
        var pending = NewRun();
        Assert.Throws<InvalidOperationException>(pending.MarkExpired);

        var failed = NewRun();
        failed.MarkFailed("boom", DateTimeOffset.UtcNow);
        Assert.Throws<InvalidOperationException>(failed.MarkExpired);
    }

    [Fact]
    public void MarkExpired_succeeds_from_succeeded()
    {
        var run = NewRun();
        run.MarkSucceeded("k", "x.csv", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        run.MarkExpired();

        Assert.Equal(ExportRunStatus.Expired, run.Status);
    }
}
