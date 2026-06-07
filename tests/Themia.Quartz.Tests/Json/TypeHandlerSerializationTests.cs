using System;
using System.Text;
using Themia.Quartz;
using Themia.Quartz.Dashboard;
using Themia.Quartz.Dashboard.TypeHandlers;
using Xunit;

namespace Themia.Quartz.Tests.Json;

/// <summary>
/// Pins the wire format produced by <see cref="TypeHandlerService.Serialize"/> /
/// <see cref="TypeHandlerService.Deserialize"/> (now System.Text.Json) for every concrete
/// <see cref="TypeHandlerBase"/> subtype. Originally captured against the Newtonsoft implementation;
/// this class is the permanent compatibility gate — the format must not drift across serializers.
/// </summary>
public sealed class TypeHandlerSerializationTests
{
    // Build a real Services graph (Handlebars + all standard types registered) using the same
    // path as production code. Services.Create internally constructs TypeHandlerService which
    // registers all StandardTypes + UnsupportedTypeHandler.
    private static Services CreateServices(Action<ThemiaQuartzOptions>? configure = null)
    {
        var options = new ThemiaQuartzOptions();
        configure?.Invoke(options);
        return Services.Create(options);
    }

    // Helper: base64-decode the serialized token and return the inner JSON string.
    private static string DecodeJson(string base64) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(base64));

    // Helper: round-trip a handler and return both the decoded JSON and the deserialized handler.
    private static (string json, TypeHandlerBase result) RoundTrip(TypeHandlerService svc, TypeHandlerBase handler)
    {
        var token = svc.Serialize(handler);
        var json = DecodeJson(token);
        var result = svc.Deserialize(token);
        return (json, result);
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────
    // StringHandler — exact wire format locked here
    // ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StringHandler_RoundTrips_WithCorrectTypeId()
    {
        var svc = CreateServices().TypeHandlers;
        var handler = new StringHandler { Name = "String" };

        var (json, result) = RoundTrip(svc, handler);

        Assert.IsType<StringHandler>(result);
        Assert.Equal("String", result.Name);
        Assert.Equal(handler.TypeId, result.TypeId);
    }

    [Fact]
    public void StringHandler_SerializedJson_ContainsDiscriminatorAndPascalCaseProperties()
    {
        var svc = CreateServices().TypeHandlers;
        var handler = new StringHandler { Name = "String" };

        var (json, _) = RoundTrip(svc, handler);

        // Discriminator must be present with full type name
        Assert.Contains("\"TypeId\":", json);
        Assert.Contains("Themia.Quartz.Dashboard.TypeHandlers.StringHandler", json);

        // Properties must be PascalCase (PropertyNamingPolicy = null)
        Assert.Contains("\"Name\":", json);
        Assert.Contains("\"DisplayName\":", json);
        Assert.Contains("\"IsMultiline\":", json);
    }

    [Fact]
    public void StringHandler_ExactWireJson_MatchesExpected()
    {
        // This is the canonical wire format. The STJ migration MUST produce an identical string.
        // PROPERTY ORDER: JsonSubTypes injects TypeId discriminator AFTER subclass properties but
        // BEFORE base-class properties. So the order is: StringHandler props → TypeId → TypeHandlerBase props.
        var svc = CreateServices().TypeHandlers;
        var handler = new StringHandler { Name = "String" };
        handler.DisplayName = "String"; // ThemiaQuartzOptions ctor sets DisplayName via Name setter

        var token = svc.Serialize(handler);
        var json = DecodeJson(token);

        // DefaultIgnoreCondition.WhenWritingNull: nulls omitted. IsMultiline=false is a bool (non-null) → included.
        // Property order: IsMultiline (subclass) → TypeId (injected by JsonSubTypes) → Name → DisplayName (base).
        Assert.Equal(
            "{\"IsMultiline\":false,\"TypeId\":\"Themia.Quartz.Dashboard.TypeHandlers.StringHandler\",\"Name\":\"String\",\"DisplayName\":\"String\"}",
            json);
    }

    [Fact]
    public void MultilineStringHandler_ExactWireJson_MatchesExpected()
    {
        var svc = CreateServices().TypeHandlers;
        var handler = new StringHandler { Name = "MultilineString", IsMultiline = true };
        handler.DisplayName = "String (Multiline)";

        var token = svc.Serialize(handler);
        var json = DecodeJson(token);

        Assert.Equal(
            "{\"IsMultiline\":true,\"TypeId\":\"Themia.Quartz.Dashboard.TypeHandlers.StringHandler\",\"Name\":\"MultilineString\",\"DisplayName\":\"String (Multiline)\"}",
            json);
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────
    // EnumHandler — System.Type field wire format locked here
    // ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EnumHandler_RoundTrips_PreservesEnumType()
    {
        var svc = CreateServices(o =>
        {
            // EnumHandler is not in StandardTypes by default — register it explicitly.
            o.StandardTypes.Add(new EnumHandler(typeof(DayOfWeek)));
        }).TypeHandlers;

        var handler = new EnumHandler(typeof(DayOfWeek)) { Name = typeof(DayOfWeek).FullName! };

        var (json, result) = RoundTrip(svc, handler);

        var enumResult = Assert.IsType<EnumHandler>(result);
        Assert.Equal(typeof(DayOfWeek), enumResult.EnumType);
        Assert.Equal(handler.TypeId, enumResult.TypeId);
    }

    [Fact]
    public void EnumHandler_SerializedJson_ContainsEnumTypeAsAssemblyQualifiedName()
    {
        var svc = CreateServices(o =>
        {
            o.StandardTypes.Add(new EnumHandler(typeof(DayOfWeek)));
        }).TypeHandlers;

        var handler = new EnumHandler(typeof(DayOfWeek)) { Name = typeof(DayOfWeek).FullName! };

        var (json, _) = RoundTrip(svc, handler);

        // Discriminator
        Assert.Contains("\"TypeId\":\"Themia.Quartz.Dashboard.TypeHandlers.EnumHandler\"", json);

        // System.Type is serialized as its assembly-qualified name (via SystemTypeJsonConverter) — the critical
        // format the STJ custom converter must reproduce. The value must start with the CLR
        // full name and include the assembly name (version-sensitive part intentionally left out).
        Assert.Contains("\"EnumType\":", json);
        Assert.Contains("System.DayOfWeek", json);
        Assert.Contains("System.Private.CoreLib", json);
    }

    [Fact]
    public void EnumHandler_EnumType_SerializedAs_AssemblyQualifiedName_NotSimpleName()
    {
        // Pin the KEY structural fact: Type is written as its AssemblyQualifiedName string,
        // NOT as a simple name. The STJ migration must use a matching custom converter.
        var svc = CreateServices(o =>
        {
            o.StandardTypes.Add(new EnumHandler(typeof(DayOfWeek)));
        }).TypeHandlers;

        var handler = new EnumHandler(typeof(DayOfWeek));
        var token = svc.Serialize(handler);
        var json = DecodeJson(token);

        // The value must be the assembly-qualified name, not just "DayOfWeek" or "System.DayOfWeek"
        var expectedFragment = $"\"EnumType\":\"{typeof(DayOfWeek).AssemblyQualifiedName}\"";
        Assert.Contains(expectedFragment, json);
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────
    // NumberHandler (all variants)
    // ──────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(NumberHandler.UnderlyingType.Decimal)]
    [InlineData(NumberHandler.UnderlyingType.Double)]
    [InlineData(NumberHandler.UnderlyingType.Float)]
    [InlineData(NumberHandler.UnderlyingType.Integer)]
    [InlineData(NumberHandler.UnderlyingType.Long)]
    public void NumberHandler_RoundTrips_PreservesNumberType(NumberHandler.UnderlyingType numberType)
    {
        var svc = CreateServices().TypeHandlers;
        var handler = new NumberHandler(numberType);

        var (json, result) = RoundTrip(svc, handler);

        var numResult = Assert.IsType<NumberHandler>(result);
        Assert.Equal(numberType, numResult.NumberType);
        Assert.Equal(handler.TypeId, numResult.TypeId);

        // Discriminator present, PascalCase
        Assert.Contains("\"TypeId\":", json);
        Assert.Contains("\"NumberType\":", json);
        Assert.Contains("Themia.Quartz.Dashboard.TypeHandlers.NumberHandler", json);
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────
    // BooleanHandler
    // ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BooleanHandler_RoundTrips_Correctly()
    {
        var svc = CreateServices().TypeHandlers;
        var handler = new BooleanHandler { Name = "Boolean" };

        var (json, result) = RoundTrip(svc, handler);

        Assert.IsType<BooleanHandler>(result);
        Assert.Equal("Boolean", result.Name);
        Assert.Contains("\"TypeId\":\"Themia.Quartz.Dashboard.TypeHandlers.BooleanHandler\"", json);
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────
    // DateTimeHandler (all three variants)
    // ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DateTimeHandler_Date_RoundTrips_Correctly()
    {
        var svc = CreateServices().TypeHandlers;
        var handler = new DateTimeHandler { Name = "Date", DisplayName = "Date", IgnoreTimeComponent = true };

        var (json, result) = RoundTrip(svc, handler);

        var dtResult = Assert.IsType<DateTimeHandler>(result);
        Assert.True(dtResult.IgnoreTimeComponent);
        Assert.False(dtResult.IsUtc);
        Assert.Contains("\"IgnoreTimeComponent\":true", json);
        Assert.Contains("Themia.Quartz.Dashboard.TypeHandlers.DateTimeHandler", json);
    }

    [Fact]
    public void DateTimeHandler_DateTime_RoundTrips_Correctly()
    {
        var svc = CreateServices().TypeHandlers;
        var handler = new DateTimeHandler { Name = "DateTime" };

        var (_, result) = RoundTrip(svc, handler);

        var dtResult = Assert.IsType<DateTimeHandler>(result);
        Assert.False(dtResult.IgnoreTimeComponent);
        Assert.False(dtResult.IsUtc);
    }

    [Fact]
    public void DateTimeHandler_DateTimeUtc_RoundTrips_Correctly()
    {
        var svc = CreateServices().TypeHandlers;
        var handler = new DateTimeHandler { Name = "DateTimeUtc", DisplayName = "DateTime (UTC)", IsUtc = true };

        var (json, result) = RoundTrip(svc, handler);

        var dtResult = Assert.IsType<DateTimeHandler>(result);
        Assert.True(dtResult.IsUtc);
        Assert.Contains("\"IsUtc\":true", json);
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────
    // FileHandler
    // ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FileHandler_RoundTrips_Correctly()
    {
        var svc = CreateServices().TypeHandlers;
        var handler = new FileHandler { Name = "File", DisplayName = "Binary Data" };

        var (json, result) = RoundTrip(svc, handler);

        Assert.IsType<FileHandler>(result);
        Assert.Equal("File", result.Name);
        Assert.Contains("\"TypeId\":\"Themia.Quartz.Dashboard.TypeHandlers.FileHandler\"", json);
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────
    // UnsupportedTypeHandler
    // ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void UnsupportedTypeHandler_RoundTrips_Correctly()
    {
        var svc = CreateServices().TypeHandlers;
        var handler = new UnsupportedTypeHandler
        {
            Name = "Unsupported",
            AssemblyQualifiedName = "Some.Type, Some.Assembly",
            StringValue = "raw value",
        };

        var (json, result) = RoundTrip(svc, handler);

        var unsupported = Assert.IsType<UnsupportedTypeHandler>(result);
        Assert.Equal("Some.Type, Some.Assembly", unsupported.AssemblyQualifiedName);
        Assert.Equal("raw value", unsupported.StringValue);
        Assert.Contains("\"AssemblyQualifiedName\":", json);
        Assert.Contains("\"StringValue\":", json);
        Assert.Contains("Themia.Quartz.Dashboard.TypeHandlers.UnsupportedTypeHandler", json);
    }

    [Fact]
    public void UnsupportedTypeHandler_NullValues_AreOmitted_FromJson()
    {
        // WhenWritingNull: null AssemblyQualifiedName and StringValue must be absent.
        var svc = CreateServices().TypeHandlers;
        var handler = new UnsupportedTypeHandler { Name = "Unsupported" };

        var (json, _) = RoundTrip(svc, handler);

        Assert.DoesNotContain("\"AssemblyQualifiedName\":", json);
        Assert.DoesNotContain("\"StringValue\":", json);
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────
    // All StandardTypes round-trip (parametric sweep)
    // ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AllStandardTypes_RoundTrip_WithSameTypeId()
    {
        var options = new ThemiaQuartzOptions();
        var svc = Services.Create(options).TypeHandlers;

        foreach (var handler in options.StandardTypes)
        {
            var token = svc.Serialize(handler);
            var result = svc.Deserialize(token);

            Assert.Equal(handler.TypeId, result.TypeId);
            Assert.Equal(handler.GetType(), result.GetType());
        }
    }
}
