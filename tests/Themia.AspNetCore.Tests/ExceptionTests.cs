using System.Collections.Generic;
using Themia.AspNetCore.Exceptions;
using Xunit;

namespace Themia.AspNetCore.Tests;

public sealed class ExceptionTests
{
    [Fact]
    public void ThemiaException_carries_message_errorCode_metadata()
    {
        var meta = new Dictionary<string, object?> { ["k"] = "v" };
        var ex = new NotFoundException("nope", errorCode: "E1", metadata: meta);

        Assert.Equal("nope", ex.Message);
        Assert.Equal("E1", ex.ErrorCode);
        Assert.Equal("v", ex.Metadata!["k"]);
    }
}
