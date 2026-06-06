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

    [Fact]
    public void FromException_ErrorHash_DiffersForSameTypeAndMessage_WhenSourceDiffers_AndStackTraceIsNull()
    {
        // Un-thrown exceptions have null StackTrace, so the fallback signature is used.
        // Two exceptions that share Type+Message but differ in Source must produce different hashes
        // (otherwise every un-thrown InvalidOperationException("x") from different callers rolls up into one row).
        var ex1 = new InvalidOperationException("same message") { Source = "ModuleA" };
        var ex2 = new InvalidOperationException("same message") { Source = "ModuleB" };

        // Confirm both have null StackTrace (pre-condition for the fallback path being exercised).
        Assert.Null(ex1.StackTrace);
        Assert.Null(ex2.StackTrace);

        var entry1 = ExceptionEntryFactory.FromException(ex1, "App");
        var entry2 = ExceptionEntryFactory.FromException(ex2, "App");

        Assert.NotEqual(entry1.ErrorHash, entry2.ErrorHash);
    }

    [Fact]
    public void FromException_ErrorHash_IsDeterministic_ForSameInputsWithNullStackTrace()
    {
        var ex1 = new InvalidOperationException("same message") { Source = "ModuleA" };
        var ex2 = new InvalidOperationException("same message") { Source = "ModuleA" };

        var entry1 = ExceptionEntryFactory.FromException(ex1, "App");
        var entry2 = ExceptionEntryFactory.FromException(ex2, "App");

        Assert.Equal(entry1.ErrorHash, entry2.ErrorHash);
    }
}
