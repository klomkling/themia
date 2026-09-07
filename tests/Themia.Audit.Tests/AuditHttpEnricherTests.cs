using System.Net;
using Microsoft.AspNetCore.Http;
using Themia.Audit.Http;
using Xunit;

namespace Themia.Audit.Tests;

public class AuditHttpEnricherTests
{
    [Fact]
    public void Reads_the_caller_address_and_user_agent_from_the_current_request()
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        ctx.Request.Headers.UserAgent = "test-agent/1.0";

        var enriched = new AuditHttpEnricher(Accessor(ctx)).Enrich(Valid());

        Assert.Equal("203.0.113.7", enriched.IpAddress);
        Assert.Equal("test-agent/1.0", enriched.UserAgent);
    }

    [Fact]
    public void Records_nulls_outside_a_request_rather_than_throwing()
    {
        // A Quartz worker host has no HttpContext. Nulls are the correct answer, not an error (spec §10f).
        var enriched = new AuditHttpEnricher(Accessor(null)).Enrich(Valid());

        Assert.Null(enriched.IpAddress);
        Assert.Null(enriched.UserAgent);
    }

    [Fact]
    public void Does_not_overwrite_an_ip_address_the_caller_already_supplied()
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");

        var enriched = new AuditHttpEnricher(Accessor(ctx)).Enrich(Valid() with { IpAddress = "10.1.1.1" });

        Assert.Equal("10.1.1.1", enriched.IpAddress);
    }

    [Fact]
    public void Does_not_overwrite_a_user_agent_the_caller_already_supplied()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.UserAgent = "test-agent/1.0";

        var enriched = new AuditHttpEnricher(Accessor(ctx)).Enrich(Valid() with { UserAgent = "caller-agent" });

        Assert.Equal("caller-agent", enriched.UserAgent);
    }

    private static AuditEntry Valid() => new()
    {
        EventType = "TEST_EVENT",
        Category = AuditCategory.Activity,
        Outcome = AuditOutcome.Success,
    };

    private static IHttpContextAccessor Accessor(HttpContext? context) =>
        new HttpContextAccessor { HttpContext = context };
}
