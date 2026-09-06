using System;
using Themia.Audit.Redaction;
using Xunit;

namespace Themia.Audit.Tests;

public class AuditRedactorTests
{
    [Theory]
    [InlineData("""{"password":"hunter2"}""")]
    [InlineData("""{"outer":{"apiKey":"abc"}}""")]                       // nested object
    [InlineData("""{"items":[{"token":"t1"},{"token":"t2"}]}""")]        // array of objects
    [InlineData("""{"a":{"b":{"c":{"ssn":"123-45-6789"}}}}""")]          // deep
    public void Redacts_secrets_at_any_depth(string json)
    {
        var result = new AuditRedactor(new AuditRedactionOptions()).Redact(json);
        Assert.DoesNotContain("hunter2", result, StringComparison.Ordinal);
        Assert.DoesNotContain("abc", result, StringComparison.Ordinal);
        Assert.DoesNotContain("t1", result, StringComparison.Ordinal);
        Assert.DoesNotContain("123-45-6789", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Keeps_the_property_name_so_its_presence_is_auditable()
        => Assert.Contains("password", new AuditRedactor(new AuditRedactionOptions())
            .Redact("""{"password":"hunter2"}"""), StringComparison.Ordinal);

    [Fact]
    public void Adopter_patterns_add_and_never_remove_defaults()
    {
        var o = new AuditRedactionOptions();
        o.AddPattern("internal_ref");
        var r = new AuditRedactor(o).Redact("""{"internal_ref":"x","password":"y"}""");
        Assert.DoesNotContain("\"x\"", r, StringComparison.Ordinal);
        Assert.DoesNotContain("\"y\"", r, StringComparison.Ordinal);
    }

    [Fact]
    public void Matches_default_patterns_case_insensitively()
    {
        var result = new AuditRedactor(new AuditRedactionOptions())
            .Redact("""{"PASSWORD":"hunter2","ApiKey":"abc"}""");
        Assert.DoesNotContain("hunter2", result, StringComparison.Ordinal);
        Assert.DoesNotContain("abc", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Leaves_non_matching_properties_untouched()
    {
        var result = new AuditRedactor(new AuditRedactionOptions())
            .Redact("""{"username":"alice","count":3}""");
        Assert.Contains(""""username":"alice"""", result, StringComparison.Ordinal);
        Assert.Contains(""""count":3"""", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Redacts_every_default_pattern()
    {
        const string json = """
            {
              "password":"a","passwordhash":"a","passwordsalt":"a","secret":"a","token":"a",
              "refreshtoken":"a","accesstoken":"a","apikey":"a","api_key":"a","authorization":"a",
              "otp":"a","pin":"a","cvv":"a","creditcard":"a","card_number":"a","privatekey":"a","ssn":"a"
            }
            """;

        var result = new AuditRedactor(new AuditRedactionOptions()).Redact(json);
        Assert.DoesNotContain("\"a\"", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_rejects_null_options()
        => Assert.Throws<ArgumentNullException>(() => new AuditRedactor(null!));
}
