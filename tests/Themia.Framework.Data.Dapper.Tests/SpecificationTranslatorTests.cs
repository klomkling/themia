using SqlKata;
using SqlKata.Compilers;
using Themia.Framework.Data.Abstractions.Exceptions;
using Themia.Framework.Data.Abstractions.Specifications;
using Themia.Framework.Data.Dapper.Mapping;
using Themia.Framework.Data.Dapper.Translation;
using Xunit;

namespace Themia.Framework.Data.Dapper.Tests;

public sealed class SpecificationTranslatorTests
{
    private sealed class Asset
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? Status { get; set; }
        public int Quantity { get; set; }
    }

    // Minimal concrete spec exposing the protected fluent Where for tests.
    private sealed class SpecBuilder : Specification<Asset>
    {
        public SpecBuilder Filter(System.Linq.Expressions.Expression<System.Func<Asset, bool>> c) { Where(c); return this; }
        public SpecBuilder Order(System.Linq.Expressions.Expression<System.Func<Asset, object?>> k, bool desc = false) { if (desc) OrderByDescending(k); else OrderBy(k); return this; }
        public SpecBuilder Paging(int? skip, int? take) { Page(skip, take); return this; }
    }
    private static SpecBuilder Spec() => new();

    private static readonly EntityMapping Map = EntityMapping.ForConvention<Asset>();
    private static SqlResult Compile(ISpecification<Asset> spec)
    {
        var query = new Query(Map.Table);
        SpecificationTranslator.Apply(query, spec, Map);
        return new PostgresCompiler().Compile(query);
    }

    [Fact] public void Equality_OnInt_EmitsParameterizedWhere()
    {
        var r = Compile(Spec().Filter(a => a.Quantity == 5));
        Assert.Contains("\"quantity\"", r.Sql);
        Assert.Contains(5, r.NamedBindings.Values);
    }

    [Fact] public void Comparisons_Emit_Operators()
    {
        var r = Compile(Spec().Filter(a => a.Quantity >= 3));
        Assert.Contains("\"quantity\"", r.Sql);
        Assert.Contains(">=", r.Sql);
        Assert.Contains(3, r.NamedBindings.Values);
    }

    [Fact] public void StringContains_EmitsLike_WithWildcards()
    {
        var r = Compile(Spec().Filter(a => a.Name.Contains("rig")));
        Assert.Contains("like", r.Sql.ToLowerInvariant());
        Assert.Contains("%rig%", r.NamedBindings.Values);
    }

    [Fact] public void StringStartsWith_And_EndsWith()
    {
        Assert.Contains("abc%", Compile(Spec().Filter(a => a.Name.StartsWith("abc"))).NamedBindings.Values);
        Assert.Contains("%xyz", Compile(Spec().Filter(a => a.Name.EndsWith("xyz"))).NamedBindings.Values);
    }

    [Fact] public void NullCheck_EmitsIsNull_AndIsNotNull()
    {
        Assert.Contains("is null", Compile(Spec().Filter(a => a.Status == null)).Sql.ToLowerInvariant());
        Assert.Contains("is not null", Compile(Spec().Filter(a => a.Status != null)).Sql.ToLowerInvariant());
    }

    [Fact] public void CollectionContains_EmitsIn()
    {
        var ids = new[] { 1, 2, 3 };
        var r = Compile(Spec().Filter(a => ids.Contains(a.Id)));
        Assert.Contains("in (", r.Sql.ToLowerInvariant());
        Assert.Contains(2, r.NamedBindings.Values);
    }

    [Fact] public void AndOr_Nest_Correctly()
    {
        var r = Compile(Spec().Filter(a => a.Quantity > 0 && (a.Status == "x" || a.Name == "y")));
        var sql = r.Sql.ToLowerInvariant();
        Assert.Contains("and", sql);
        Assert.Contains("or", sql);
    }

    [Fact] public void Not_Negates()
    {
        var r = Compile(Spec().Filter(a => !(a.Quantity == 5)));
        Assert.Contains("not", r.Sql.ToLowerInvariant());
    }

    [Fact] public void OrderBy_And_Paging_Emit()
    {
        var r = Compile(Spec().Filter(a => a.Quantity > 0).Order(a => a.Name).Paging(10, 5));
        var sql = r.Sql.ToLowerInvariant();
        Assert.Contains("order by", sql);
        Assert.Contains("limit", sql);
        Assert.Contains("offset", sql);
    }

    [Fact] public void CapturedVariable_BecomesParameter_NotInlined()
    {
        var threshold = 42;
        var r = Compile(Spec().Filter(a => a.Quantity == threshold));
        Assert.Contains(42, r.NamedBindings.Values);
        Assert.DoesNotContain("42", r.Sql);   // value is parameterized, not inlined into SQL text
    }

    [Fact] public void UnsupportedConstruct_Throws()
    {
        Assert.Throws<UnsupportedSpecificationException>(() =>
            Compile(Spec().Filter(a => a.Quantity.ToString() == "5")));
    }

    [Fact] public void NestedMemberAccess_Throws()
    {
        Assert.Throws<UnsupportedSpecificationException>(() =>
            Compile(Spec().Filter(a => a.Name.Length == 3)));   // member-of-member, not a direct entity column
    }
}
