using Themia.Modules.Notifications.Outbox;
using Themia.Notifications;
using Xunit;

namespace Themia.Modules.Notifications.Tests.Outbox;

/// <summary>
/// The single serialization point for the email options an outbox row carries. Everything the outbox
/// stores beyond recipient/subject/body goes through here, so a column swap or a rename is one edit
/// rather than one per dialect.
/// </summary>
public sealed class NotificationDeliveryOptionsTests
{
    [Fact]
    public void Serialize_ReturnsNull_WhenNothingIsSet()
    {
        // Null, not "{}": the column stays NULL for every message that sets no options, which is every
        // message enqueued before this feature and every caller that never adopts it.
        Assert.Null(NotificationDeliveryOptions.Serialize(null, null, null, null));
    }

    [Fact]
    public void Serialize_ReturnsNull_WhenEveryCollectionIsEmpty()
        => Assert.Null(NotificationDeliveryOptions.Serialize([], [], null, new Dictionary<string, string>()));

    [Fact]
    public void RoundTrip_PreservesAllFour()
    {
        var json = NotificationDeliveryOptions.Serialize(
            cc: ["cc1@example.com", "cc2@example.com"],
            bcc: ["bcc@example.com"],
            plainTextBody: "plain\n\nbody",
            headers: new Dictionary<string, string> { ["X-SES-CONFIGURATION-SET"] = "transactional" });

        Assert.NotNull(json);
        var restored = NotificationDeliveryOptions.Deserialize(json);

        Assert.NotNull(restored);
        Assert.Equal(["cc1@example.com", "cc2@example.com"], restored.Cc);
        Assert.Equal(["bcc@example.com"], restored.Bcc);
        Assert.Equal("plain\n\nbody", restored.PlainTextBody);
        Assert.Equal("transactional", restored.Headers!["X-SES-CONFIGURATION-SET"]);
    }

    [Fact]
    public void RoundTrip_DoesNotConfuseCcWithBcc()
    {
        // The reason this type exists. Positional tuple mapping in three hand-written dialects put five
        // consecutive string? values next to each other; swapping cc and bcc compiled, passed, and sent
        // the blind copies visibly. JSON field names make that swap unrepresentable — this test fails if
        // the property names are ever crossed.
        var json = NotificationDeliveryOptions.Serialize(
            cc: ["visible@example.com"], bcc: ["hidden@example.com"], plainTextBody: null, headers: null);

        var restored = NotificationDeliveryOptions.Deserialize(json)!;

        Assert.Equal(["visible@example.com"], restored.Cc);
        Assert.Equal(["hidden@example.com"], restored.Bcc);
    }

    [Fact]
    public void Serialize_OmitsWhatWasNotSet()
    {
        var json = NotificationDeliveryOptions.Serialize(null, null, "just text", null)!;

        Assert.DoesNotContain("cc", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("headers", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("plainTextBody", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_ReturnsNull_ForNullOrBlank()
    {
        Assert.Null(NotificationDeliveryOptions.Deserialize(null));
        Assert.Null(NotificationDeliveryOptions.Deserialize("   "));
    }

    [Fact]
    public void Deserialize_ThrowsJsonException_OnCorruptJson()
    {
        // Deliberately propagates rather than returning null. A corrupt row is a permanent condition the
        // dispatcher must dead-letter; swallowing it here would send the message with its options
        // silently dropped, which is the failure this whole feature exists to remove.
        Assert.ThrowsAny<System.Text.Json.JsonException>(
            () => NotificationDeliveryOptions.Deserialize("{not json"));
    }

    [Fact]
    public void ApplyTo_BuildsAMessageCarryingEveryOption()
    {
        var json = NotificationDeliveryOptions.Serialize(
            ["cc@example.com"], ["bcc@example.com"], "plain",
            new Dictionary<string, string> { ["X-Custom"] = "v" });

        var message = NotificationDeliveryOptions.Deserialize(json)!.ApplyTo(new NotificationMessage
        {
            Channel = NotificationChannel.Email,
            Recipient = "to@example.com",
            Subject = "s",
            Body = "b",
        });

        Assert.Equal(["cc@example.com"], message.Cc);
        Assert.Equal(["bcc@example.com"], message.Bcc);
        Assert.Equal("plain", message.PlainTextBody);
        Assert.Equal("v", message.Headers!["X-Custom"]);
        Assert.Equal("to@example.com", message.Recipient);   // the base message is not disturbed
        Assert.Equal("b", message.Body);
    }

    [Fact]
    public void ApplyTo_PropagatesValidationFailure_ForAPoisonedRow()
    {
        // A row whose stored header carries CR/LF — hand-edited, or written by something that bypassed
        // NotificationMessage. Rehydration re-runs the same validation, so the injection cannot enter
        // through the outbox either. The dispatcher turns this into a permanent failure.
        var poisoned = NotificationDeliveryOptions.Deserialize(
            """{"headers":{"X-Bad":"v\r\nBcc: attacker@example.com"}}""")!;

        Assert.Throws<ArgumentException>(() => poisoned.ApplyTo(new NotificationMessage
        {
            Channel = NotificationChannel.Email,
            Recipient = "to@example.com",
            Body = "b",
        }));
    }
}
