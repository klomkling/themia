using System.Collections.Generic;
using Themia.Quartz;
using Themia.Quartz.Dashboard;
using Themia.Quartz.Dashboard.TypeHandlers;
using Xunit;

namespace Themia.Quartz.Tests.Json;

/// <summary>
/// Pins the JSON produced by the Handlebars <c>{{json}}</c> helper registered in
/// <see cref="HandlebarsHelpers"/>. The helper calls <c>JsonSerializer.Serialize(argument)</c>
/// with default options (PascalCase, nulls included). These tests lock that casing + null-inclusion
/// contract that the dashboard templates depend on — a permanent compatibility pin.
/// </summary>
public sealed class HandlebarsJsonHelperTests
{
    // Services.Create registers all Handlebars helpers including {{json}}.
    private static Services CreateServices() => Services.Create(new ThemiaQuartzOptions());

    // Compile and render a one-expression template.
    private static string Render(Services svc, string template, object model)
    {
        var compiled = svc.Handlebars.Compile(template);
        return compiled(model);
    }

    [Fact]
    public void JsonHelper_SerializesAnonymousObject_WithPascalCaseKeys()
    {
        var svc = CreateServices();
        // The helper uses default STJ options: PascalCase, nulls included
        var model = new { Name = "hello", Value = 42 };
        var output = Render(svc, "{{json this}}", model);

        Assert.Equal("{\"Name\":\"hello\",\"Value\":42}", output);
    }

    [Fact]
    public void JsonHelper_IncludesNullValues_UnlikeTypeHandlerService()
    {
        // Critical distinction: TypeHandlerService uses DefaultIgnoreCondition.WhenWritingNull (omits
        // nulls), but the {{json}} helper uses DEFAULT options which INCLUDE nulls.
        var svc = CreateServices();
        var model = new { Name = (string?)null, Value = 1 };
        var output = Render(svc, "{{json this}}", model);

        Assert.Contains("\"Name\":null", output);
    }

    [Fact]
    public void JsonHelper_SerializesBooleans_AsLowercaseLiterals()
    {
        var svc = CreateServices();
        var model = new { IsActive = true, IsDeleted = false };
        var output = Render(svc, "{{json this}}", model);

        Assert.Contains("\"IsActive\":true", output);
        Assert.Contains("\"IsDeleted\":false", output);
    }

    [Fact]
    public void JsonHelper_SerializesStringHandler_WithPascalCaseProperties()
    {
        // Pin the shape as rendered by the {{json}} helper (PascalCase, nulls in).
        // This is distinct from TypeHandlerService.Serialize which omits nulls.
        var svc = CreateServices();
        var handler = new StringHandler { Name = "String" };
        handler.DisplayName = "String"; // set via Name setter
        var output = Render(svc, "{{json this}}", handler);

        // PascalCase keys
        Assert.Contains("\"Name\":", output);
        Assert.Contains("\"DisplayName\":", output);
        Assert.Contains("\"IsMultiline\":", output);
        Assert.Contains("\"TypeId\":", output);

        // TypeId is the full type name
        Assert.Contains("Themia.Quartz.Dashboard.TypeHandlers.StringHandler", output);
    }

    [Fact]
    public void JsonHelper_SerializesDictionary_WithStringKeys()
    {
        var svc = CreateServices();
        var dict = new Dictionary<string, object?> { ["alpha"] = 1, ["beta"] = "two" };
        var output = Render(svc, "{{json this}}", dict);

        Assert.Contains("\"alpha\":", output);
        Assert.Contains("\"beta\":", output);
    }

    [Fact]
    public void JsonHelper_SerializesTypeHandlerAndStringValue_ReflectsDefaultSerialization()
    {
        // Mirrors the RenderView model: { Value, StringValue, TypeHandler }
        // Used in .hbs templates to emit type-handler state as a JS object.
        var svc = CreateServices();
        var handler = new StringHandler { Name = "String" };
        handler.DisplayName = "String";

        var model = new
        {
            Value = "hello",
            StringValue = "hello",
            TypeHandler = handler,
        };

        var output = Render(svc, "{{json this}}", model);

        // Top-level keys are PascalCase
        Assert.Contains("\"Value\":", output);
        Assert.Contains("\"StringValue\":", output);
        Assert.Contains("\"TypeHandler\":", output);

        // Nested TypeHandler object has its own TypeId
        Assert.Contains("Themia.Quartz.Dashboard.TypeHandlers.StringHandler", output);
    }

    [Fact]
    public void JsonHelper_DoesNotEscapeNonAsciiOrPlus()
    {
        // Pins the UnsafeRelaxedJsonEscaping encoder on _jsonHelperOptions.
        // The {{json}} helper output is injected RAW into HTML/JS templates (WriteSafeString /
        // triple-stache). Newtonsoft emitted + and non-ASCII chars literally; STJ's default
        // encoder would escape them to \uXXXX. This test fails with the default encoder and
        // passes only with UnsafeRelaxedJsonEscaping — proving the divergence is fixed.
        var svc = CreateServices();
        var model = new { Label = "(UTC+07:00) Café" };
        var output = Render(svc, "{{json this}}", model);

        // Must contain literal + (not +) and literal é (not é).
        Assert.Contains("+", output);
        Assert.Contains("é", output);
        // Full value check
        Assert.Contains("(UTC+07:00) Café", output);
    }
}
