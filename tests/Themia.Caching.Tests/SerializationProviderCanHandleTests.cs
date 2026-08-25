using Xunit;
using Themia.Caching;

namespace Themia.Caching.Tests;

/// <summary>
/// <see cref="ISerializationProvider.CanHandle"/> answers "would this type serialize at all", without
/// an instance, so a startup validator can report a permanently-uncacheable response type before a
/// request ever reaches it. coord #0100.
/// </summary>
public sealed class SerializationProviderCanHandleTests
{
    public sealed record Facet(int Id, string? Name, int Count);

    [Fact]
    public void MessagePack_should_reject_an_interface_typed_collection()
    {
        // The exact shape that made caching a silent no-op for a consumer: a plain positional record
        // returned as IReadOnlyList<T>. MessagePack has no formatter for the interface.
        Assert.False(new MessagePackSerializationProvider().CanHandle(typeof(IReadOnlyList<Facet>)));
    }

    [Fact]
    public void MessagePack_should_accept_a_type_it_can_serialize()
    {
        Assert.True(new MessagePackSerializationProvider().CanHandle(typeof(string)));
    }

    [Fact]
    public void Json_should_accept_the_shape_MessagePack_rejects()
    {
        // Same type, different serializer: this is the fix a consumer applies, so the validator must
        // agree that it is fine. JSON does not override CanHandle - its reflection serializer handles
        // essentially anything, so it takes the interface default, which is reached through the
        // interface rather than the concrete type.
        ISerializationProvider provider = new JsonSerializationProvider();

        Assert.True(provider.CanHandle(typeof(IReadOnlyList<Facet>)));
    }

    [Fact]
    public void A_provider_that_does_not_override_CanHandle_should_report_everything_as_handled()
    {
        // Default interface method. A third-party provider cannot answer, so it must not produce a
        // false alarm - the runtime warning still covers it.
        ISerializationProvider provider = new UnknowingProvider();

        Assert.True(provider.CanHandle(typeof(IReadOnlyList<Facet>)));
    }

    private sealed class UnknowingProvider : ISerializationProvider
    {
        public byte[] Serialize<T>(T value) => [];

        public T? Deserialize<T>(byte[] data) => default;
    }
}
