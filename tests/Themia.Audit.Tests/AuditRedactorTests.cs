using System;
using Themia.Audit.Redaction;
using Xunit;

namespace Themia.Audit.Tests;

public class AuditRedactorTests
{
    private static AuditRedactor Redactor => new(new AuditRedactionOptions());

    [Theory]
    [InlineData("""{"password":"SEC-1"}""", "SEC-1")]
    [InlineData("""{"outer":{"apiKey":"SEC-2"}}""", "SEC-2")]                  // nested object
    [InlineData("""{"a":{"b":{"c":{"ssn":"SEC-3"}}}}""", "SEC-3")]             // deep
    public void Redacts_a_secret_at_any_depth(string json, string secret)
    {
        var result = Redactor.Redact(json);
        Assert.DoesNotContain(secret, result, StringComparison.Ordinal);
        Assert.Contains("[redacted]", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Redacts_every_element_of_an_array_not_only_the_first()
    {
        var result = Redactor.Redact("""{"items":[{"token":"SEC-A"},{"token":"SEC-B"},{"token":"SEC-C"}]}""");
        Assert.DoesNotContain("SEC-A", result, StringComparison.Ordinal);
        Assert.DoesNotContain("SEC-B", result, StringComparison.Ordinal);
        Assert.DoesNotContain("SEC-C", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Keeps_the_property_name_so_its_presence_is_auditable()
        => Assert.Contains("password", Redactor.Redact("""{"password":"hunter2"}"""), StringComparison.Ordinal);

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
        var result = Redactor.Redact("""{"PASSWORD":"hunter2","ApiKey":"abc"}""");
        Assert.DoesNotContain("hunter2", result, StringComparison.Ordinal);
        Assert.DoesNotContain("abc", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Leaves_non_matching_properties_untouched()
    {
        var result = Redactor.Redact("""{"username":"alice","count":3}""");
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

        var result = Redactor.Redact(json);
        Assert.DoesNotContain("\"a\"", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_rejects_null_options()
        => Assert.Throws<ArgumentNullException>(() => new AuditRedactor(null!));

    [Fact]
    public void IsSensitive_matches_regardless_of_case()
    {
        var options = new AuditRedactionOptions();

        Assert.True(options.IsSensitive("PASSWORD"));
        Assert.True(options.IsSensitive("Password"));
        Assert.True(options.IsSensitive("password"));
    }

    [Fact]
    public void IsSensitive_still_reports_a_default_pattern_after_a_custom_pattern_is_added()
    {
        var options = new AuditRedactionOptions();
        options.AddPattern("internal_ref");

        Assert.True(options.IsSensitive("internal_ref"));
        Assert.True(options.IsSensitive("password"));
    }

    [Theory]
    [InlineData("""{"password":null,"other":null}""")]           // null values
    [InlineData("""{}""")]                                       // empty object
    [InlineData("""{"password":"first","password":"second"}""")] // duplicate property names
    [InlineData("""[{"password":"x"},{"password":"y"}]""")]      // bare array root
    [InlineData("\"just a string\"")]                            // bare string root
    [InlineData("""42""")]                                       // bare number root
    public void Round_trips_without_throwing_on_unusual_but_valid_json(string json)
    {
        var exception = Record.Exception(() => Redactor.Redact(json));
        Assert.Null(exception);
    }

    [Fact]
    public void Bare_string_root_passes_through_unchanged_which_is_why_AuditRecorder_rejects_string_payloads()
    {
        // A bare-string JSON root has no property name for IsSensitive to see, so a secret inside it is
        // never touched — this is the exact hole AuditRecorder.RecordCoreAsync closes by rejecting a
        // `string` payload outright (see AuditRecorderTests.Rejects_a_string_payload) rather than letting
        // it reach this redactor at all.
        const string json = "\"hunter2\"";

        var result = Redactor.Redact(json);

        Assert.Equal(json, result);
    }

    [Theory]
    [InlineData("newPassword")]
    [InlineData("currentPassword")]
    [InlineData("client_secret")]
    [InlineData("id_token")]
    [InlineData("access_token_hash")]
    public void Redacts_a_default_pattern_embedded_in_a_longer_property_name(string propertyName)
    {
        var json = $$"""{"{{propertyName}}":"SECRET"}""";

        var result = Redactor.Redact(json);

        Assert.DoesNotContain("SECRET", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("newPassword")]
    [InlineData("currentPassword")]
    [InlineData("client_secret")]
    [InlineData("id_token")]
    [InlineData("access_token_hash")]
    public void IsSensitive_matches_a_pattern_embedded_in_a_longer_property_name(string propertyName)
    {
        var options = new AuditRedactionOptions();

        Assert.True(options.IsSensitive(propertyName));
    }

    [Theory]
    [InlineData("tokenCount")]
    [InlineData("passwordChangedAt")]
    public void IsSensitive_also_flags_a_name_that_merely_contains_a_pattern(string propertyName)
    {
        // Pinning the deliberate trade-off: substring matching redacts these too, even though neither
        // name carries a secret by itself. Under-redaction (the previous exact-match behaviour, which let
        // "newPassword" and "client_secret" through untouched) is treated as the worse failure.
        var options = new AuditRedactionOptions();

        Assert.True(options.IsSensitive(propertyName));
    }
}
