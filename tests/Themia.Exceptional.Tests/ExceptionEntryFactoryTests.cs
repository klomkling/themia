using System.Text.Json;
using Themia.Exceptional;
using Xunit;

namespace Themia.Exceptional.Tests;

public class ExceptionEntryFactoryTests
{
    [Fact]
    public void FromException_PopulatesCoreFields()
    {
        Exception captured;
        try { throw new InvalidOperationException("boom"); }
        catch (Exception e) { captured = e; }

        var entry = ExceptionEntryFactory.FromException(captured, "MyApp");

        Assert.Equal("MyApp", entry.ApplicationName);
        Assert.Equal("System.InvalidOperationException", entry.Type);
        Assert.Equal("boom", entry.Message);
        Assert.False(string.IsNullOrEmpty(entry.ErrorHash));
        Assert.Equal(1, entry.DuplicateCount);
        Assert.NotEqual(default, entry.Guid);
    }

    [Fact]
    public void FromException_FullJson_RehydratesMessage()
    {
        Exception captured;
        try { throw new InvalidOperationException("boom"); }
        catch (Exception e) { captured = e; }

        var entry = ExceptionEntryFactory.FromException(captured, "MyApp");

        using var doc = JsonDocument.Parse(entry.Detail);
        Assert.Equal("boom", doc.RootElement.GetProperty("Message").GetString());
    }

    [Fact]
    public void FromException_SerializesExceptionData_WithoutThrowing()
    {
        var ex = new InvalidOperationException("with data");
        ex.Data["key"] = "value";

        var entry = ExceptionEntryFactory.FromException(ex, "MyApp");

        using var doc = JsonDocument.Parse(entry.Detail);
        Assert.Equal("value", doc.RootElement.GetProperty("Data").GetProperty("key").GetString());
    }

    [Fact]
    public void FromException_ThrowsArgumentException_WhenApplicationNameIsWhiteSpace()
    {
        var ex = new InvalidOperationException("x");
        Assert.Throws<ArgumentException>(() => ExceptionEntryFactory.FromException(ex, "   "));
    }

    [Fact]
    public void FromException_ThrowsArgumentNullException_WhenExceptionIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => ExceptionEntryFactory.FromException(null!, "App"));
    }
}
