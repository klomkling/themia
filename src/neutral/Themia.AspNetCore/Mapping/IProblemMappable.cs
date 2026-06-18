namespace Themia.AspNetCore.Mapping;

/// <summary>Implement on a consumer exception so <c>ProblemDetailsMiddleware</c> maps it to the Themia
/// ProblemDetails contract — without the consumer adopting Themia's sealed exception types.</summary>
public interface IProblemMappable
{
    /// <summary>Describes how this exception should be surfaced as a problem response.</summary>
    ProblemMapping ToProblemMapping();
}
