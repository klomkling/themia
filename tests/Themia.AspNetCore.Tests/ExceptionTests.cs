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

    [Fact]
    public void Validation_and_external_carry_extra_fields()
    {
        var v = new ValidationException("Email", "bad", errorCode: "INVALID");
        Assert.Equal("Email", v.PropertyName);
        Assert.Equal("INVALID", v.ErrorCode);

        var e = new ExternalServiceException("payments", "down");
        Assert.Equal("payments", e.ServiceName);
    }

    [Fact]
    public void ExternalService_carries_errorCode_and_metadata()
    {
        var meta = new Dictionary<string, object?> { ["region"] = "ap-southeast-1" };
        var inner = new System.TimeoutException("timed out");
        var e = new ExternalServiceException("payments", "down", errorCode: "PAY_503", metadata: meta, innerException: inner);

        Assert.Equal("payments", e.ServiceName);
        Assert.Equal("PAY_503", e.ErrorCode);
        Assert.Equal("ap-southeast-1", e.Metadata!["region"]);
        Assert.Same(inner, e.InnerException);
    }
}
