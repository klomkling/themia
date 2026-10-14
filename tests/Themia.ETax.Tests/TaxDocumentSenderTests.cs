using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Themia.ETax.Tests;

public sealed class TaxDocumentSenderTests
{
    private readonly Flow _flow = new();

    private TimeSpan Lease => _flow.Options.Lease;

    [Fact]
    public async Task RunOnceAsync_ShouldSendTheOriginalOnceAndMarkTheDocumentSent()
    {
        var doc = await _flow.IssuedAsync();

        Assert.Equal(1, await _flow.Sender.RunOnceAsync());

        var email = Assert.Single(_flow.Mailer.Sent);
        Assert.Equal(doc.Id, email.DocumentId);
        Assert.Equal("buyer@example.test", email.To);
        Assert.Equal("rd@example.test", email.RevenueDepartmentAddress);
        Assert.Contains("data-marker=\"original\"", _flow.Pdf.Htmls[^1]);
        var row = await _flow.GetAsync(doc.Id);
        Assert.Equal(TaxDocumentState.Sent, row.State);
        Assert.Equal(doc.Id.ToString(), row.MessageId);
        Assert.NotNull(row.PdfSha256);
        Assert.Single(await _flow.Store.GetEvidenceAsync(doc.Id));
    }

    [Fact]
    public async Task RunOnceAsync_ShouldDoNothing_WhenNothingIsDueOrAlreadySent()
    {
        await _flow.IssuedAsync();
        await _flow.Sender.RunOnceAsync();

        Assert.Equal(0, await _flow.Sender.RunOnceAsync());
        Assert.Single(_flow.Mailer.Sent);
    }

    [Fact]
    public async Task RunOnceAsync_ShouldRetryATransientFailureAfterTheBackoff_AndKeepTheSameOriginal()
    {
        var doc = await _flow.IssuedAsync();
        _flow.Mailer.Then(() => TaxDocumentSendResult.Transient("smtp_4xx"));

        await _flow.Sender.RunOnceAsync();

        var afterFailure = await _flow.GetAsync(doc.Id);
        Assert.Equal(TaxDocumentState.OriginalPrepared, afterFailure.State);
        Assert.Equal(1, afterFailure.Attempts);
        Assert.Equal("smtp_4xx", afterFailure.LastErrorCode);
        Assert.True(afterFailure.NextAttemptAt > _flow.Clock.GetUtcNow());
        Assert.Equal(0, await _flow.Sender.RunOnceAsync());

        _flow.Clock.Advance(_flow.Options.MaxBackoff);
        await _flow.Sender.RunOnceAsync();

        Assert.Equal(TaxDocumentState.Sent, (await _flow.GetAsync(doc.Id)).State);
        Assert.Equal(2, _flow.Mailer.Sent.Count);
        Assert.Equal(_flow.Mailer.Sent[0].Pdf, _flow.Mailer.Sent[1].Pdf);
        Assert.Equal(_flow.Mailer.Sent[0].DocumentId, _flow.Mailer.Sent[1].DocumentId);
        Assert.Equal(2, (await _flow.Store.GetEvidenceAsync(doc.Id)).Count);
    }

    [Fact]
    public async Task RunOnceAsync_ShouldFailAndAlertOnce_WhenTransientErrorsReachTheAttemptLimit()
    {
        var doc = await _flow.IssuedAsync();
        _flow.Mailer.Default = () => TaxDocumentSendResult.Transient("smtp_4xx");

        for (var i = 0; i < _flow.Options.MaxAttempts; i++)
        {
            await _flow.Sender.RunOnceAsync();
            _flow.Clock.Advance(_flow.Options.MaxBackoff);
        }

        var row = await _flow.GetAsync(doc.Id);
        Assert.Equal(TaxDocumentState.Failed, row.State);
        Assert.Equal(_flow.Options.MaxAttempts, row.Attempts);
        var failure = Assert.Single(_flow.Failures.Failures);
        Assert.Equal(new TaxDocumentFailure(doc.Id, TaxDocumentKind.TaxInvoiceReceipt, "smtp_4xx", _flow.Options.MaxAttempts), failure);
        Assert.Single(_flow.Logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("failed after"));
        Assert.Equal(0, await _flow.Sender.RunOnceAsync());
    }

    [Fact]
    public async Task RunOnceAsync_ShouldFailAtOnce_WhenTheErrorIsPermanent()
    {
        var doc = await _flow.IssuedAsync();
        _flow.Mailer.Then(() => TaxDocumentSendResult.Permanent("smtp_5xx"));

        await _flow.Sender.RunOnceAsync();

        var row = await _flow.GetAsync(doc.Id);
        Assert.Equal(TaxDocumentState.Failed, row.State);
        Assert.Equal("smtp_5xx", row.LastErrorCode);
        Assert.Single(_flow.Failures.Failures);
    }

    [Fact]
    public async Task RunOnceAsync_ShouldTreatAThrowingMailerAsTransient()
    {
        var doc = await _flow.IssuedAsync();
        _flow.Mailer.Then(() => throw new InvalidOperationException("boom"));

        await _flow.Sender.RunOnceAsync();

        var row = await _flow.GetAsync(doc.Id);
        Assert.Equal(TaxDocumentState.OriginalPrepared, row.State);
        Assert.Equal("mailer_exception", row.LastErrorCode);
    }

    [Fact]
    public async Task RunOnceAsync_ShouldTreatARenderFailureAsTransient_AndSendNothing()
    {
        var doc = await _flow.IssuedAsync();
        _flow.Pdf.Throw = new TimeoutException("render timed out");

        await _flow.Sender.RunOnceAsync();

        var row = await _flow.GetAsync(doc.Id);
        Assert.Equal(TaxDocumentState.Issued, row.State);
        Assert.Equal("render_error", row.LastErrorCode);
        Assert.Equal(1, row.Attempts);
        Assert.Empty(_flow.Mailer.Sent);
    }

    [Fact]
    public async Task RunOnceAsync_ShouldFailLoud_WhenTheSameRendererVersionProducesDifferentBytes()
    {
        var doc = await _flow.IssuedAsync();
        _flow.Mailer.Then(() => TaxDocumentSendResult.Transient("smtp_4xx"));
        await _flow.Sender.RunOnceAsync();
        _flow.Pdf.Body = _ => "DIFFERENT";
        _flow.Clock.Advance(_flow.Options.MaxBackoff);

        await _flow.Sender.RunOnceAsync();

        var row = await _flow.GetAsync(doc.Id);
        Assert.Equal(TaxDocumentState.Failed, row.State);
        Assert.Equal("render_mismatch", row.LastErrorCode);
        Assert.Single(_flow.Mailer.Sent);
        Assert.Single(_flow.Failures.Failures);
    }

    [Fact]
    public async Task RunOnceAsync_ShouldWarnRecordNewEvidenceAndSend_WhenTheRendererVersionChanged()
    {
        var doc = await _flow.IssuedAsync();
        _flow.Mailer.Then(() => TaxDocumentSendResult.Transient("smtp_4xx"));
        await _flow.Sender.RunOnceAsync();
        _flow.Pdf.Version = "chrome-2";
        _flow.Pdf.Body = _ => "NEW-BUILD-BYTES";
        _flow.Clock.Advance(_flow.Options.MaxBackoff);

        await _flow.Sender.RunOnceAsync();

        Assert.Equal(TaxDocumentState.Sent, (await _flow.GetAsync(doc.Id)).State);
        Assert.Contains(_flow.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("new renderer version"));
        var evidence = await _flow.Store.GetEvidenceAsync(doc.Id);
        Assert.Equal(["chrome-1|n1", "chrome-2|n1"], evidence.Select(e => e.RendererVersion));
        Assert.Equal(evidence[0].PdfSha256, (await _flow.GetAsync(doc.Id)).PdfSha256);
    }

    [Fact]
    public async Task RunOnceAsync_ShouldResendTheSameOriginal_WhenTheSendSucceededButTheLeaseWasLost()
    {
        var doc = await _flow.IssuedAsync();
        _flow.Store.LoseNextMarkSent = true;
        await _flow.Sender.RunOnceAsync();
        Assert.Equal(TaxDocumentState.OriginalPrepared, (await _flow.GetAsync(doc.Id)).State);
        _flow.Clock.Advance(Lease + Lease);

        await _flow.Sender.RunOnceAsync();

        Assert.Equal(TaxDocumentState.Sent, (await _flow.GetAsync(doc.Id)).State);
        Assert.Equal(2, _flow.Mailer.Sent.Count);
        Assert.Equal(_flow.Mailer.Sent[0].Pdf, _flow.Mailer.Sent[1].Pdf);
        Assert.Equal(_flow.Mailer.Sent[0].DocumentId, _flow.Mailer.Sent[1].DocumentId);
    }

    // ---- F2: cancel vs worker race ------------------------------------------------------------------------------

    private async Task<TaxDocumentCancelOutcome> TryCancelAsync(Guid id)
    {
        await using var tx = new InMemoryTaxDocumentStore.FakeTransaction();
        var outcome = await _flow.InMemory.CancelForReplacementAsync(tx, id, Guid.NewGuid(), Flow.Seller, _flow.Clock.GetUtcNow());
        tx.Commit();
        return outcome;
    }

    [Fact]
    public async Task RunOnceAsync_ShouldNotSendACancelledDocument_WhenItWasCancelledAfterItsLeaseLapsedBeforeTheSend()
    {
        var doc = await _flow.IssuedAsync();
        TaxDocumentCancelOutcome? cancel = null;
        _flow.Store.BeforeBeginSend = async () =>
        {
            _flow.Clock.Advance(Lease + TimeSpan.FromSeconds(1));
            cancel = await TryCancelAsync(doc.Id);
        };

        await _flow.Sender.RunOnceAsync();

        Assert.Equal(TaxDocumentCancelOutcome.Cancelled, cancel);
        Assert.Empty(_flow.Mailer.Sent);
        Assert.Equal(TaxDocumentState.Cancelled, (await _flow.GetAsync(doc.Id)).State);
    }

    [Fact]
    public async Task RunOnceAsync_ShouldNotRecordAnOriginal_WhenTheDocumentWasCancelledWhileRendering()
    {
        var doc = await _flow.IssuedAsync();
        _flow.Store.BeforeRecordOriginal = async () =>
        {
            _flow.Clock.Advance(Lease + TimeSpan.FromSeconds(1));
            await TryCancelAsync(doc.Id);
        };

        await _flow.Sender.RunOnceAsync();

        Assert.Empty(_flow.Mailer.Sent);
        Assert.Empty(await _flow.Store.GetEvidenceAsync(doc.Id));
    }

    [Fact]
    public async Task Cancel_ShouldBeRefused_WhileTheMailerIsSending()
    {
        var doc = await _flow.IssuedAsync();
        TaxDocumentCancelOutcome? duringSend = null;
        _flow.Mailer.Then(() =>
        {
            duringSend = TryCancelAsync(doc.Id).GetAwaiter().GetResult();
            return TaxDocumentSendResult.Sent;
        });

        await _flow.Sender.RunOnceAsync();

        Assert.Equal(TaxDocumentCancelOutcome.LeaseHeld, duringSend);
        Assert.Equal(TaxDocumentState.Sent, (await _flow.GetAsync(doc.Id)).State);
    }

    [Fact]
    public async Task RunOnceAsync_ShouldNeverClaimACancelledDocument()
    {
        var doc = await _flow.IssuedAsync();
        _flow.Clock.Advance(TimeSpan.FromMinutes(5));
        await TryCancelAsync(doc.Id);

        Assert.Equal(0, await _flow.Sender.RunOnceAsync());
        Assert.Empty(_flow.Mailer.Sent);
    }

    [Fact]
    public async Task RunOnceAsync_ShouldSendOnce_WhenTwoSendersRunAtTheSameTime()
    {
        await _flow.IssuedAsync();
        var second = new TaxDocumentSender(
            _flow.Store, _flow.Mailer, _flow.Failures, _flow.Renderer, _flow.Options, _flow.Clock, new CapturingLogger<TaxDocumentSender>(_flow.Logs));

        var claimed = await Task.WhenAll(_flow.Sender.RunOnceAsync(), second.RunOnceAsync());

        Assert.Equal(1, claimed.Sum());
        Assert.Single(_flow.Mailer.Sent);
    }

    // ---- B1: a stalled mailer must not outlive the lease ---------------------------------------------------------

    private static Flow FlowWithSendTimeout() =>
        new(new TaxDocumentSendOptions { RevenueDepartmentAddress = "rd@example.test", SendTimeout = TimeSpan.FromMilliseconds(150) });

    [Fact]
    public async Task RunOnceAsync_ShouldCancelAMailerThatHonoursTheTokenAndCountSendTimeout()
    {
        var flow = FlowWithSendTimeout();
        var doc = await flow.IssuedAsync();
        var sawCancellation = new TaskCompletionSource();
        flow.Mailer.ThenAsync(async ct =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { sawCancellation.SetResult(); throw; }
            return TaxDocumentSendResult.Sent;
        });

        await flow.Sender.RunOnceAsync();

        var row = await flow.GetAsync(doc.Id);
        await sawCancellation.Task.WaitAsync(TimeSpan.FromSeconds(10)); // the mailer's token must be cancelled when SendTimeout elapses
        Assert.Equal(TaxDocumentState.OriginalPrepared, row.State);
        Assert.Equal("send_timeout", row.LastErrorCode);
        Assert.Equal(1, row.Attempts);
    }

    [Fact]
    public async Task RunOnceAsync_ShouldStopWaitingForAMailerThatIgnoresTheToken()
    {
        var flow = FlowWithSendTimeout();
        var doc = await flow.IssuedAsync();
        var never = new TaskCompletionSource<TaxDocumentSendResult>();
        flow.Mailer.ThenAsync(_ => never.Task);

        await flow.Sender.RunOnceAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("send_timeout", (await flow.GetAsync(doc.Id)).LastErrorCode);
    }

    private static async Task<TaxDocumentCancelOutcome> CancelAtAsync(Flow flow, Guid id)
    {
        await using var tx = new InMemoryTaxDocumentStore.FakeTransaction();
        var outcome = await flow.InMemory.CancelForReplacementAsync(tx, id, Guid.NewGuid(), Flow.Seller, flow.Clock.GetUtcNow());
        tx.Commit();
        return outcome;
    }

    [Fact]
    public async Task RunOnceAsync_ShouldKeepTheLeaseAfterASendTimeout_SoACancelCannotSlipInUntilItLapses()
    {
        var flow = FlowWithSendTimeout();
        var doc = await flow.IssuedAsync();
        flow.Mailer.ThenAsync(_ => new TaskCompletionSource<TaxDocumentSendResult>().Task); // ignores its token, never answers

        await flow.Sender.RunOnceAsync();

        var row = await flow.GetAsync(doc.Id);
        Assert.Equal("send_timeout", row.LastErrorCode);
        Assert.NotNull(row.ClaimToken);
        Assert.True(row.NextAttemptAt >= row.ClaimExpiresAt, "The retry must not be due before the lease lapses.");
        Assert.Equal(TaxDocumentCancelOutcome.LeaseHeld, await CancelAtAsync(flow, doc.Id));
        Assert.Equal(0, await flow.Sender.RunOnceAsync());

        flow.Clock.Advance(flow.Options.Lease + TimeSpan.FromSeconds(1));
        Assert.Equal(TaxDocumentCancelOutcome.Cancelled, await CancelAtAsync(flow, doc.Id));
    }

    [Fact]
    public async Task RunOnceAsync_ShouldRetryAfterTheLeaseLapses_WhenASendTimedOut()
    {
        var flow = FlowWithSendTimeout();
        var doc = await flow.IssuedAsync();
        flow.Mailer.ThenAsync(_ => new TaskCompletionSource<TaxDocumentSendResult>().Task);
        await flow.Sender.RunOnceAsync();

        flow.Clock.Advance(flow.Options.Lease + flow.Options.MaxBackoff);
        await flow.Sender.RunOnceAsync();

        Assert.Equal(TaxDocumentState.Sent, (await flow.GetAsync(doc.Id)).State);
        Assert.Equal(2, flow.Mailer.Sent.Count);
    }

    [Fact]
    public async Task RunOnceAsync_ShouldKeepTheLeaseEvenWhenTheTimeoutExhaustsTheAttempts()
    {
        var flow = new Flow(new TaxDocumentSendOptions
        {
            RevenueDepartmentAddress = "rd@example.test",
            SendTimeout = TimeSpan.FromMilliseconds(150),
            MaxAttempts = 1,
        });
        var doc = await flow.IssuedAsync();
        flow.Mailer.ThenAsync(_ => new TaskCompletionSource<TaxDocumentSendResult>().Task);

        await flow.Sender.RunOnceAsync();

        Assert.Equal(TaxDocumentState.Failed, (await flow.GetAsync(doc.Id)).State);
        Assert.Equal(TaxDocumentCancelOutcome.LeaseHeld, await CancelAtAsync(flow, doc.Id));
        flow.Clock.Advance(flow.Options.Lease + TimeSpan.FromSeconds(1));
        Assert.Equal(TaxDocumentCancelOutcome.Cancelled, await CancelAtAsync(flow, doc.Id));
    }

    [Fact]
    public async Task RunOnceAsync_ShouldReleaseTheLeaseAtOnce_ForOrdinaryTransientFailures()
    {
        var doc = await _flow.IssuedAsync();
        _flow.Mailer.Then(() => TaxDocumentSendResult.Transient("smtp_4xx"));

        await _flow.Sender.RunOnceAsync();

        Assert.Null((await _flow.GetAsync(doc.Id)).ClaimToken);
    }

    [Fact]
    public async Task RunOnceAsync_ShouldObserveAnAbandonedMailerThatLaterFaults_WithoutLeakingItsMessage()
    {
        var flow = FlowWithSendTimeout();
        await flow.IssuedAsync();
        var mailer = new TaskCompletionSource<TaxDocumentSendResult>();
        flow.Mailer.ThenAsync(_ => mailer.Task);
        var unobserved = 0;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e) => Interlocked.Increment(ref unobserved);
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await flow.Sender.RunOnceAsync();
            mailer.SetException(new InvalidOperationException("550 mailbox pii-late@example.test unavailable"));
            await WaitForLogAsync(flow, "abandoned send later faulted");

            mailer = null!;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.Equal(0, unobserved);
            Assert.Contains(flow.Logs.Entries, e => e.Message.Contains("InvalidOperationException"));
            Assert.DoesNotContain(flow.Logs.Entries, e => e.Message.Contains("pii-late"));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [Fact]
    public async Task RunOnceAsync_ShouldWarn_WhenAnAbandonedMailerFinishesSendingLate()
    {
        var flow = FlowWithSendTimeout();
        await flow.IssuedAsync();
        var mailer = new TaskCompletionSource<TaxDocumentSendResult>();
        flow.Mailer.ThenAsync(_ => mailer.Task);

        await flow.Sender.RunOnceAsync();
        mailer.SetResult(TaxDocumentSendResult.Sent);
        await WaitForLogAsync(flow, "completed late");

        Assert.Contains(flow.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("may have been delivered"));
    }

    private static async Task WaitForLogAsync(Flow flow, string fragment)
    {
        for (var i = 0; i < 200 && !flow.Logs.Entries.Any(e => e.Message.Contains(fragment)); i++)
            await Task.Delay(25);
        Assert.Contains(flow.Logs.Entries, e => e.Message.Contains(fragment));
    }

    [Fact]
    public async Task RunOnceAsync_ShouldPropagateCancellation_WhenTheCallerCancelsDuringTheSend()
    {
        var flow = FlowWithSendTimeout();
        await flow.IssuedAsync();
        using var cts = new CancellationTokenSource();
        flow.Mailer.ThenAsync(async ct =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return TaxDocumentSendResult.Sent;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flow.Sender.RunOnceAsync(cts.Token));
    }

    [Theory]
    [InlineData(120)]
    [InlineData(300)]
    [InlineData(0)]
    public void SendTimeout_ShouldBePositiveAndShorterThanTheLease(int seconds)
    {
        var options = new TaxDocumentSendOptions { RevenueDepartmentAddress = "rd@example.test", SendTimeout = TimeSpan.FromSeconds(seconds) };

        Assert.Throws<InvalidOperationException>(() => new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddThemiaETaxDocuments(o => { o.RevenueDepartmentAddress = options.RevenueDepartmentAddress; o.SendTimeout = options.SendTimeout; }));
    }

    // ---- permanent failures before any send ---------------------------------------------------------------------

    [Fact]
    public async Task RunOnceAsync_ShouldFailAtOnce_WhenThereIsNoRecipient()
    {
        var doc = await _flow.IssuedAsync(buyerEmail: null);

        await _flow.Sender.RunOnceAsync();

        Assert.Equal("no_recipient", (await _flow.GetAsync(doc.Id)).LastErrorCode);
        Assert.Equal(TaxDocumentState.Failed, (await _flow.GetAsync(doc.Id)).State);
    }

    [Fact]
    public async Task RunOnceAsync_ShouldBeAbleToSend_AfterTheRecipientIsCorrectedAndRequeued()
    {
        var doc = await _flow.IssuedAsync(buyerEmail: null);
        await _flow.Sender.RunOnceAsync();
        Assert.True(await _flow.Store.UpdateBuyerEmailAsync(doc.Id, Flow.Seller, "fixed@example.test"));
        Assert.True(await _flow.Service.RequeueAsync(doc.Id, Flow.Seller, "operator-1"));

        await _flow.Sender.RunOnceAsync();

        Assert.Equal(TaxDocumentState.Sent, (await _flow.GetAsync(doc.Id)).State);
        Assert.Equal("fixed@example.test", Assert.Single(_flow.Mailer.Sent).To);
    }

    [Fact]
    public async Task RunOnceAsync_ShouldFailAtOnce_WhenTheStoredSnapshotNoLongerMatchesItsHash()
    {
        var flow = new Flow();
        var good = await flow.IssuedAsync();
        // Replace the stored text with a different one but keep the original hash.
        flow.InMemory.Replace(good.Id, r => r with { SnapshotJson = r.SnapshotJson.Replace("TAX-0001", "TAX-0002") });

        await flow.Sender.RunOnceAsync();

        Assert.Equal("snapshot_integrity", (await flow.GetAsync(good.Id)).LastErrorCode);
        Assert.Empty(flow.Mailer.Sent);
    }

    [Fact]
    public async Task RunOnceAsync_ShouldFailAtOnce_WhenTheTemplateVersionIsUnknown()
    {
        var flow = new Flow();
        var doc = await flow.IssuedAsync();
        flow.InMemory.Replace(doc.Id, r => r with { TemplateVersion = "999" });

        await flow.Sender.RunOnceAsync();

        Assert.Equal("template_unsupported", (await flow.GetAsync(doc.Id)).LastErrorCode);
    }

    [Fact]
    public async Task RunOnceAsync_ShouldStillFail_WhenTheFailureHandlerThrows()
    {
        var doc = await _flow.IssuedAsync();
        _flow.Failures.Throw = new InvalidOperationException("alerting is down");
        _flow.Mailer.Then(() => TaxDocumentSendResult.Permanent("smtp_5xx"));

        await _flow.Sender.RunOnceAsync();

        Assert.Equal(TaxDocumentState.Failed, (await _flow.GetAsync(doc.Id)).State);
        Assert.Contains(_flow.Logs.Entries, e => e.Message.Contains("failure handler threw"));
    }
}
