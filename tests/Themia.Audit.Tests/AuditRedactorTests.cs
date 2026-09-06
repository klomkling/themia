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
    public void IsSensitive_matches_case_insensitively_while_Patterns_Contains_does_not()
    {
        var options = new AuditRedactionOptions();

        Assert.True(options.IsSensitive("PASSWORD"));
        Assert.True(options.IsSensitive("Password"));

        // Documents the trap Patterns exists to warn against: it returns a snapshot array, so
        // `options.Patterns.Contains(...)` binds to LINQ's Enumerable.Contains against a plain
        // Array, which compares ordinally and misses the case-insensitive match that IsSensitive
        // (and the redactor) guarantee. Captured to a local first: xUnit's own Assert.DoesNotContain
        // special-cases ICollection<T> the same way LINQ's Contains does, which for a HashSet-backed
        // collection would use its custom comparer and mask the very trap this test locks down —
        // matching that would silently stop testing what it claims to.
        var linqContainsResult = options.Patterns.Contains("PASSWORD");
        Assert.False(linqContainsResult);
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
}
