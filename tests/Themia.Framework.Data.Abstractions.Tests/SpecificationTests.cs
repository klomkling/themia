using System.Linq.Expressions;
using Themia.Framework.Data.Abstractions.Specifications;
using Xunit;

namespace Themia.Framework.Data.Abstractions.Tests;

public sealed class SpecificationTests
{
    private sealed record Person(string Name, int Age);

    private sealed class AdultSpec : Specification<Person>
    {
        public AdultSpec() { Where(p => p.Age >= 18); OrderBy(p => p.Name); }
    }

    [Fact]
    public void Where_SetsCriteria_ThatCompilesAndFilters()
    {
        var spec = new AdultSpec();
        var predicate = spec.Criteria!.Compile();
        Assert.True(predicate(new Person("A", 20)));
        Assert.False(predicate(new Person("B", 10)));
    }

    [Fact]
    public void And_CombinesTwoCriteria_WithLogicalAnd()
    {
        ISpecification<Person> spec = new AdultSpec().And(p => p.Name.StartsWith("A"));
        var predicate = spec.Criteria!.Compile();
        Assert.True(predicate(new Person("Ann", 30)));
        Assert.False(predicate(new Person("Bob", 30)));
        Assert.False(predicate(new Person("Amy", 10)));
    }

    [Fact]
    public void Or_CombinesTwoCriteria_WithLogicalOr()
    {
        ISpecification<Person> spec = new AdultSpec().Or(p => p.Name == "Kid");
        var predicate = spec.Criteria!.Compile();
        Assert.True(predicate(new Person("Kid", 5)));
        Assert.True(predicate(new Person("Zed", 40)));
        Assert.False(predicate(new Person("Tom", 5)));
    }

    [Fact]
    public void Not_NegatesCriteria()
    {
        ISpecification<Person> spec = new AdultSpec().Not();
        var predicate = spec.Criteria!.Compile();
        Assert.True(predicate(new Person("Kid", 5)));
        Assert.False(predicate(new Person("Adult", 30)));
    }

    [Fact]
    public void Page_SetsSkipAndTake()
    {
        var spec = new AdultSpec();
        ((Specification<Person>)spec).Page(skip: 10, take: 5);
        Assert.Equal(10, spec.Skip);
        Assert.Equal(5, spec.Take);
    }
}
