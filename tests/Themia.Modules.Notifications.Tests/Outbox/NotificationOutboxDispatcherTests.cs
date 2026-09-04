using Microsoft.Extensions.DependencyInjection;

using Themia.Messaging.Outbox;
using Themia.Modules.Notifications.Outbox;
using Themia.Notifications;

using Xunit;

namespace Themia.Modules.Notifications.Tests.Outbox;

/// <summary>
/// Pins how each <see cref="NotificationOutcome"/> maps onto a <see cref="DispatchResult"/>.
/// </summary>
/// <remarks>
/// This file exists because of a real regression. When <see cref="NotificationOutcome.NotConfigured"/>
/// was first added, the dispatcher still read only <c>result.Succeeded</c>, so a "nothing was sent, no
/// provider is configured" result was mapped to <see cref="DispatchOutcome.Transient"/> — every
/// notification on a host running without a configured provider was retried to the attempt cap and then
/// permanently dead-lettered, losing messages that previously completed. The whole suite stayed green
/// because the integration tests inject their own recording sender, so the development stub never went
/// through the outbox even once. These tests close that gap.
/// </remarks>
public class NotificationOutboxDispatcherTests
{
    [Fact]
    public async Task Sent_IsDelivered()
    {
        var result = await DispatchWith(NotificationResult.Success("provider-id"));

        Assert.Equal(DispatchOutcome.Delivered, result.Outcome);
        Assert.Null(result.Error);
    }

    // A provider rejection may survive a later attempt, so it retries.
    [Fact]
    public async Task Failed_IsTransient_AndKeepsTheError()
    {
        var result = await DispatchWith(NotificationResult.Failure("provider said no"));

        Assert.Equal(DispatchOutcome.Transient, result.Outcome);
        Assert.Equal("provider said no", result.Error);
    }

    // THE REGRESSION THIS FILE EXISTS FOR. Configuration cannot change between backoff attempts, so
    // retrying burns the attempt cap to reach the same dead-letter with five times the log noise.
    // Permanent fails on the first attempt and puts the reason in last_error immediately.
    [Fact]
    public async Task NotConfigured_IsPermanent_NotTransient()
    {
        var result = await DispatchWith(NotificationResult.NoProviderConfigured("no IEmailSender is configured"));

        Assert.Equal(DispatchOutcome.Permanent, result.Outcome);
        Assert.Equal("no IEmailSender is configured", result.Error);
    }

    // The real default DI graph, not a hand-made stub: AddThemiaNotifications() TryAdds the logger
    // senders, so this is what an adopter who never configured a provider actually gets. Before the fix
    // this returned Transient and the row dead-lettered after five attempts.
    [Fact]
    public async Task TheDefaultRegistrationsProduceAPermanentResult_NotARetryLoop()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThemiaNotifications();

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var result = await new NotificationOutboxDispatcher()
            .DispatchAsync(scope.ServiceProvider, Row(), CancellationToken.None);

        Assert.Equal(DispatchOutcome.Permanent, result.Outcome);
        Assert.Contains("configured", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<DispatchResult> DispatchWith(NotificationResult senderResult)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmailSender>(new StubEmailSender(senderResult));

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        return await new NotificationOutboxDispatcher()
            .DispatchAsync(scope.ServiceProvider, Row(), CancellationToken.None);
    }

    private static ClaimedOutboxRow Row() => new(
        Id: Guid.CreateVersion7(),
        TenantId: null,
        Channel: NotificationChannel.Email,
        Recipient: "a@b.com",
        Subject: "s",
        Body: "hi",
        Attempts: 0);

    private sealed class StubEmailSender(NotificationResult result) : IEmailSender
    {
        public Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    // --- delivery options rehydrated from the row (cc / bcc / plain-text alternative / headers) ---

    [Fact]
    public async Task DeliveryOptions_ReachTheSender()
    {
        var json = NotificationDeliveryOptions.Serialize(
            ["cc@example.com"], ["bcc@example.com"], "plain",
            new Dictionary<string, string> { ["X-SES-CONFIGURATION-SET"] = "transactional" });

        var (result, seen) = await DispatchCapturing(Row() with { DeliveryOptions = json });

        Assert.Equal(DispatchOutcome.Delivered, result.Outcome);
        Assert.Equal(["cc@example.com"], seen!.Cc);
        Assert.Equal(["bcc@example.com"], seen.Bcc);
        Assert.Equal("plain", seen.PlainTextBody);
        Assert.Equal("transactional", seen.Headers!["X-SES-CONFIGURATION-SET"]);
        Assert.Equal("a@b.com", seen.Recipient);   // the row's own columns still win
        Assert.Equal("hi", seen.Body);
    }

    [Fact]
    public async Task NoDeliveryOptions_SendsExactlyWhatItAlwaysDid()
    {
        var (result, seen) = await DispatchCapturing(Row());

        Assert.Equal(DispatchOutcome.Delivered, result.Outcome);
        Assert.Null(seen!.Cc);
        Assert.Null(seen.Bcc);
        Assert.Null(seen.PlainTextBody);
        Assert.Null(seen.Headers);
    }

    // A corrupt column is a PERMANENT condition: the row cannot repair itself between attempts, so
    // retrying burns the attempt cap to reach the same dead-letter with five times the log noise. Same
    // reasoning as NotConfigured above, and the same class of bug this whole file was written for.
    [Fact]
    public async Task CorruptDeliveryOptions_IsPermanent()
    {
        var (result, seen) = await DispatchCapturing(Row() with { DeliveryOptions = "{not json" });

        Assert.Equal(DispatchOutcome.Permanent, result.Outcome);
        Assert.Null(seen);   // nothing was handed to the provider
    }

    // A stored header carrying CR/LF — hand-edited, or written by something that bypassed
    // NotificationMessage. Rehydration re-runs the same validation, so the injection cannot enter through
    // the outbox either, and the row is dead-lettered rather than retried forever.
    [Fact]
    public async Task PoisonedHeaderInDeliveryOptions_IsPermanent_AndNeverReachesTheSender()
    {
        var row = Row() with { DeliveryOptions = """{"headers":{"X-Bad":"v\r\nBcc: attacker@example.com"}}""" };

        var (result, seen) = await DispatchCapturing(row);

        Assert.Equal(DispatchOutcome.Permanent, result.Outcome);
        Assert.Null(seen);
    }

    private static async Task<(DispatchResult Result, NotificationMessage? Seen)> DispatchCapturing(ClaimedOutboxRow row)
    {
        var sender = new CapturingEmailSender();
        var services = new ServiceCollection();
        services.AddSingleton<IEmailSender>(sender);

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var result = await new NotificationOutboxDispatcher()
            .DispatchAsync(scope.ServiceProvider, row, CancellationToken.None);

        return (result, sender.Seen);
    }

    private sealed class CapturingEmailSender : IEmailSender
    {
        public NotificationMessage? Seen { get; private set; }

        public Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
        {
            Seen = message;
            return Task.FromResult(NotificationResult.Success());
        }
    }
}
