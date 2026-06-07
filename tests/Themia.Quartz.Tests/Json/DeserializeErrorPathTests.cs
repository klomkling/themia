using System;
using System.Text;
using System.Text.Json;
using Themia.Quartz;
using Themia.Quartz.Dashboard;
using Themia.Quartz.Dashboard.TypeHandlers;
using Xunit;

namespace Themia.Quartz.Tests.Json;

/// <summary>
/// Pins the fail-fast error contract of <see cref="TypeHandlerService.Deserialize"/> and the custom
/// converters: malformed / null / non-object / bad-discriminator payloads must throw (a
/// <see cref="JsonException"/> or the internal <c>UnknownTypeHandlerException</c> subclass), never
/// return null into callers that dereference it. These guard the defensive branches the STJ migration
/// added against <see cref="NullReferenceException"/> / <see cref="InvalidOperationException"/> leaks.
/// The converters are internal (no InternalsVisibleTo), so this exercises them through the public
/// <see cref="TypeHandlerService.Deserialize"/> surface — which is the right level anyway.
/// </summary>
public sealed class DeserializeErrorPathTests
{
    private static TypeHandlerService CreateService(Action<ThemiaQuartzOptions>? configure = null)
    {
        var options = new ThemiaQuartzOptions();
        configure?.Invoke(options);
        return Services.Create(options).TypeHandlers;
    }

    private static string Encode(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Deserialize_TopLevelNull_ThrowsJsonException()
    {
        // STJ returns null for a top-level null token (the polymorphic converter is bypassed), so the
        // service's own null-guard must turn that into a JsonException rather than hand null back.
        var svc = CreateService();
        Assert.ThrowsAny<JsonException>(() => svc.Deserialize(Encode("null")));
    }

    [Theory]
    [InlineData("[1,2,3]")]   // array
    [InlineData("\"hello\"")] // string
    [InlineData("42")]        // number
    public void Deserialize_NonObjectPayload_ThrowsJsonException(string json)
    {
        // TypeHandlerJsonConverter.Read guards non-object roots so TryGetProperty doesn't throw the
        // wrong exception type (InvalidOperationException) that would escape the controller's catch.
        var svc = CreateService();
        Assert.ThrowsAny<JsonException>(() => svc.Deserialize(Encode(json)));
    }

    [Fact]
    public void Deserialize_MissingTypeIdDiscriminator_ThrowsJsonException()
    {
        // Missing TypeId → UnknownTypeHandlerException (a JsonException subclass).
        var svc = CreateService();
        Assert.ThrowsAny<JsonException>(() => svc.Deserialize(Encode("{\"Name\":\"x\"}")));
    }

    [Fact]
    public void Deserialize_UnknownTypeIdDiscriminator_ThrowsJsonException()
    {
        // Unknown TypeId → UnknownTypeHandlerException (a JsonException subclass).
        var svc = CreateService();
        Assert.ThrowsAny<JsonException>(() => svc.Deserialize(Encode("{\"TypeId\":\"Not.A.Registered.Type\"}")));
    }

    [Fact]
    public void Deserialize_EnumHandler_WithEmptyEnumType_ThrowsJsonException()
    {
        // SystemTypeJsonConverter.Read fails fast on an empty Type token rather than returning null,
        // which EnumHandler.EnumType would later dereference into a NullReferenceException.
        var svc = CreateService(o => o.StandardTypes.Add(new EnumHandler(typeof(DayOfWeek))));
        var json = "{\"TypeId\":\"Themia.Quartz.Dashboard.TypeHandlers.EnumHandler\",\"EnumType\":\"\"}";
        Assert.ThrowsAny<JsonException>(() => svc.Deserialize(Encode(json)));
    }
}
