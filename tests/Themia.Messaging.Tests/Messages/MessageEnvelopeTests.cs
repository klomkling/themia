using Themia.Messaging.Messages;

using Xunit;

namespace Themia.Messaging.Tests.Messages;

public class MessageEnvelopeTests
{
    private static MessageEnvelope Valid() => new()
    {
        MessageId = Guid.CreateVersion7(),
        Type = "listing.snapshot.v1",
        Payload = "{}",
        Destination = "propertiezy",
        Origin = "ezy-assets",
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void Validate_ShouldSucceed_WhenRequiredFieldsArePresent()
    {
        var exception = Record.Exception(() => Valid().Validate());

        Assert.Null(exception);
    }

    [Fact]
    public void Validate_ShouldThrow_WhenMessageIdIsEmpty()
    {
        var envelope = Valid();
        envelope.MessageId = Guid.Empty;

        Assert.Throws<ArgumentException>(() => envelope.Validate());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_ShouldThrow_WhenTypeIsMissing(string? type)
    {
        var envelope = Valid();
        envelope.Type = type!;

        Assert.Throws<ArgumentException>(() => envelope.Validate());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_ShouldThrow_WhenDestinationIsMissing(string? destination)
    {
        var envelope = Valid();
        envelope.Destination = destination!;

        Assert.Throws<ArgumentException>(() => envelope.Validate());
    }

    // F2: Origin is no longer required on the envelope itself — a caller may leave it unset and let the
    // module's configured MessagingModuleOptions.Origin fill in at enqueue time (MessageOutboxStore).
    // Validate() has no access to that configuration, so it must not reject a blank Origin.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_ShouldSucceed_WhenOriginIsMissing(string? origin)
    {
        var envelope = Valid();
        envelope.Origin = origin!;

        var exception = Record.Exception(() => envelope.Validate());

        Assert.Null(exception);
    }

    // A version with no entity key scopes to nothing, so it would fence nothing while looking like it does.
    [Fact]
    public void Validate_ShouldThrow_WhenVersionIsSetWithoutEntityKey()
    {
        var envelope = Valid();
        envelope.Version = 7;
        envelope.EntityKey = null;

        Assert.Throws<ArgumentException>(() => envelope.Validate());
    }

    [Fact]
    public void Validate_ShouldSucceed_WhenVersionAndEntityKeyAreBothSet()
    {
        var envelope = Valid();
        envelope.Version = 7;
        envelope.EntityKey = "listing-42";

        var exception = Record.Exception(() => envelope.Validate());

        Assert.Null(exception);
    }

    // An entity key with no version is fine: it identifies the entity for diagnostics without fencing.
    [Fact]
    public void Validate_ShouldSucceed_WhenEntityKeyIsSetWithoutVersion()
    {
        var envelope = Valid();
        envelope.EntityKey = "listing-42";
        envelope.Version = null;

        var exception = Record.Exception(() => envelope.Validate());

        Assert.Null(exception);
    }

    // An empty payload is legal — a delete/tombstone event carries its meaning entirely in Type + EntityKey.
    [Fact]
    public void Validate_ShouldSucceed_WhenPayloadIsEmpty()
    {
        var envelope = Valid();
        envelope.Payload = string.Empty;

        var exception = Record.Exception(() => envelope.Validate());

        Assert.Null(exception);
    }
}
