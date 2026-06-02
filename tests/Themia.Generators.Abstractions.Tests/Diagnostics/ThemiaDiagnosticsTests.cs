using System;
using Themia.Generators.Abstractions.Diagnostics;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Themia.Generators.Abstractions.Tests.Diagnostics;

public class ThemiaDiagnosticsTests
{
    [Fact]
    public void DiagnosticIdRange_HasReservedRanges()
    {
        Assert.Equal("THEMIA001-THEMIA099", DiagnosticIdRange.ThemiaDIRange);
        Assert.Equal("THEMIA100-THEMIA199", DiagnosticIdRange.ThemiaAnalyzersRange);
        Assert.Equal("THEMIA200+", DiagnosticIdRange.ConsumerRange);
    }

    [Fact]
    public void CreateError_ProducesErrorDescriptor()
    {
        var d = ThemiaDiagnostics.CreateError("THEMIA001", "Test title", "Test message");
        Assert.Equal("THEMIA001", d.Id);
        Assert.Equal(DiagnosticSeverity.Error, d.DefaultSeverity);
        Assert.True(d.IsEnabledByDefault);
        Assert.Equal("Themia.DI", d.Category);
    }

    [Fact]
    public void CreateWarning_ProducesWarningDescriptor()
    {
        var d = ThemiaDiagnostics.CreateWarning("THEMIA004", "Title", "Message");
        Assert.Equal(DiagnosticSeverity.Warning, d.DefaultSeverity);
    }

    [Fact]
    public void CreateInfo_ProducesInfoDescriptor()
    {
        var d = ThemiaDiagnostics.CreateInfo("THEMIA050", "Title", "Message");
        Assert.Equal(DiagnosticSeverity.Info, d.DefaultSeverity);
    }

    [Theory]
    [InlineData("THEMIA")]
    [InlineData("themia001")]
    [InlineData("IDEVSGEN001")]
    public void CreateError_RejectsInvalidIdFormat(string invalidId)
    {
        Assert.Throws<ArgumentException>(() =>
            ThemiaDiagnostics.CreateError(invalidId, "Title", "Message"));
    }
}
