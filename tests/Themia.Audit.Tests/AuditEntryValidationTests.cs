using System;
using Xunit;

namespace Themia.Audit.Tests;

public class AuditEntryValidationTests
{
    private static AuditEntry Valid() => new()
    {
        EventType = "PROPOSAL_ACCEPTED",
        Category = AuditCategory.Activity,
        Outcome = AuditOutcome.Success,
    };

    [Fact]
    public void Validate_accepts_a_valid_entry()
    {
        var exception = Record.Exception(() => Valid().Validate());
        Assert.Null(exception);
    }

    [Fact]
    public void Validate_rejects_over_length_event_type()
    {
        var entry = new AuditEntry
        {
            EventType = new string('x', AuditEntry.MaxEventTypeLength + 1),
            Category = AuditCategory.Activity,
            Outcome = AuditOutcome.Success,
        };

        var ex = Assert.Throws<ArgumentException>(() => entry.Validate());
        Assert.Contains(nameof(AuditEntry.EventType), ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(AuditEntry.ActorId), AuditEntry.MaxActorIdLength)]
    [InlineData(nameof(AuditEntry.ActorName), AuditEntry.MaxActorNameLength)]
    [InlineData(nameof(AuditEntry.EntityType), AuditEntry.MaxEntityTypeLength)]
    [InlineData(nameof(AuditEntry.EntityId), AuditEntry.MaxEntityIdLength)]
    [InlineData(nameof(AuditEntry.Reason), AuditEntry.MaxReasonLength)]
    [InlineData(nameof(AuditEntry.CorrelationId), AuditEntry.MaxCorrelationIdLength)]
    public void Validate_rejects_over_length_adopter_named_field(string fieldName, int maxLength)
    {
        var overLong = new string('x', maxLength + 1);
        var entry = fieldName switch
        {
            nameof(AuditEntry.ActorId) => Valid() with { ActorId = overLong },
            nameof(AuditEntry.ActorName) => Valid() with { ActorName = overLong },
            nameof(AuditEntry.EntityType) => Valid() with { EntityType = overLong },
            nameof(AuditEntry.EntityId) => Valid() with { EntityId = overLong },
            nameof(AuditEntry.Reason) => Valid() with { Reason = overLong },
            nameof(AuditEntry.CorrelationId) => Valid() with { CorrelationId = overLong },
            _ => throw new ArgumentOutOfRangeException(nameof(fieldName)),
        };

        var ex = Assert.Throws<ArgumentException>(() => entry.Validate());
        Assert.Contains(fieldName, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_rejects_unspecified_category()
    {
        var entry = Valid() with { Category = AuditCategory.Unspecified };
        Assert.Throws<ArgumentException>(() => entry.Validate());
    }

    [Fact]
    public void Validate_rejects_unspecified_outcome()
    {
        var entry = Valid() with { Outcome = AuditOutcome.Unspecified };
        Assert.Throws<ArgumentException>(() => entry.Validate());
    }

    [Fact]
    public void Validate_rejects_out_of_range_category()
    {
        var entry = Valid() with { Category = (AuditCategory)999 };
        Assert.Throws<ArgumentException>(() => entry.Validate());
    }

    [Fact]
    public void Validate_rejects_out_of_range_outcome()
    {
        var entry = Valid() with { Outcome = (AuditOutcome)999 };
        Assert.Throws<ArgumentException>(() => entry.Validate());
    }

    [Fact]
    public void Validate_truncates_user_agent_instead_of_rejecting()
    {
        var entry = Valid() with { UserAgent = new string('u', AuditEntry.MaxUserAgentLength + 50) };
        var normalized = entry.Normalize();
        Assert.Equal(AuditEntry.MaxUserAgentLength, normalized.UserAgent!.Length);
    }

    [Fact]
    public void Normalize_truncates_ip_address_instead_of_rejecting()
    {
        var entry = Valid() with { IpAddress = new string('1', AuditEntry.MaxIpAddressLength + 10) };
        var normalized = entry.Normalize();
        Assert.Equal(AuditEntry.MaxIpAddressLength, normalized.IpAddress!.Length);
    }

    [Fact]
    public void Normalize_leaves_short_values_unchanged()
    {
        var entry = Valid() with { UserAgent = "short-agent", IpAddress = "127.0.0.1" };
        var normalized = entry.Normalize();
        Assert.Equal("short-agent", normalized.UserAgent);
        Assert.Equal("127.0.0.1", normalized.IpAddress);
    }

    [Fact]
    public void Normalize_leaves_null_user_agent_and_ip_address_null()
    {
        var normalized = Valid().Normalize();
        Assert.Null(normalized.UserAgent);
        Assert.Null(normalized.IpAddress);
    }
}
