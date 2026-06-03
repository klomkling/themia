using Themia.Framework.Core.Primitives;

namespace Themia.Framework.Core.Extensions;

/// <summary>
/// Extension methods for Result pattern using modern C# 14 features.
/// </summary>
public static class ResultExtensions
{
    /// <summary>
    /// Transforms a successful result using the provided selector function.
    /// </summary>
    /// <typeparam name="TSource">Source value type.</typeparam>
    /// <typeparam name="TResult">Result value type.</typeparam>
    /// <param name="result">The result to transform.</param>
    /// <param name="selector">Function to transform the value.</param>
    /// <returns>Transformed result or propagated failure.</returns>
    public static Result<TResult> Map<TSource, TResult>(
        this Result<TSource> result,
        Func<TSource, TResult> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);

        return result.IsSuccess
            ? Result<TResult>.Success(selector(result.Value))
            : Result<TResult>.Failure(result.Error!);
    }

    /// <summary>
    /// Chains operations on successful results (flatMap/bind operation).
    /// </summary>
    /// <typeparam name="TSource">Source value type.</typeparam>
    /// <typeparam name="TResult">Result value type.</typeparam>
    /// <param name="result">The result to bind.</param>
    /// <param name="binder">Function that returns another result.</param>
    /// <returns>Result from binder or propagated failure.</returns>
    public static Result<TResult> Bind<TSource, TResult>(
        this Result<TSource> result,
        Func<TSource, Result<TResult>> binder)
    {
        ArgumentNullException.ThrowIfNull(binder);

        return result.IsSuccess
            ? binder(result.Value)
            : Result<TResult>.Failure(result.Error!);
    }

    /// <summary>
    /// Performs an action if the result is successful.
    /// </summary>
    /// <typeparam name="T">Value type.</typeparam>
    /// <param name="result">The result.</param>
    /// <param name="action">Action to perform on success.</param>
    /// <returns>The original result for chaining.</returns>
    public static Result<T> OnSuccess<T>(this Result<T> result, Action<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (result.IsSuccess)
        {
            action(result.Value);
        }

        return result;
    }

    /// <summary>
    /// Performs an action if the result is a failure.
    /// </summary>
    /// <typeparam name="T">Value type.</typeparam>
    /// <param name="result">The result.</param>
    /// <param name="action">Action to perform on failure.</param>
    /// <returns>The original result for chaining.</returns>
    public static Result<T> OnFailure<T>(this Result<T> result, Action<Error> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (result.IsFailure)
        {
            action(result.Error!);
        }

        return result;
    }

    /// <summary>
    /// Provides a fallback value if the result is a failure.
    /// </summary>
    /// <typeparam name="T">Value type.</typeparam>
    /// <param name="result">The result.</param>
    /// <param name="fallbackValue">Value to return on failure.</param>
    /// <returns>The result value or fallback.</returns>
    public static T ValueOr<T>(this Result<T> result, T fallbackValue) =>
        result.IsSuccess ? result.Value : fallbackValue;

    /// <summary>
    /// Provides a fallback value using a factory function if the result is a failure.
    /// </summary>
    /// <typeparam name="T">Value type.</typeparam>
    /// <param name="result">The result.</param>
    /// <param name="fallbackFactory">Factory to produce value on failure.</param>
    /// <returns>The result value or fallback.</returns>
    public static T ValueOr<T>(this Result<T> result, Func<Error, T> fallbackFactory)
    {
        ArgumentNullException.ThrowIfNull(fallbackFactory);

        return result.IsSuccess ? result.Value : fallbackFactory(result.Error!);
    }

    /// <summary>
    /// Combines multiple results into a single result containing a collection.
    /// Uses C# 14 collection expressions for modern syntax.
    /// </summary>
    /// <typeparam name="T">Value type.</typeparam>
    /// <param name="results">Results to combine.</param>
    /// <returns>Success with all values, or first failure encountered.</returns>
    public static Result<IReadOnlyList<T>> Combine<T>(params Result<T>[] results)
    {
        ArgumentNullException.ThrowIfNull(results);

        List<T> values = [];

        foreach (var result in results)
        {
            if (result.IsFailure)
            {
                return Result<IReadOnlyList<T>>.Failure(result.Error!);
            }

            values.Add(result.Value);
        }

        return Result<IReadOnlyList<T>>.Success(values);
    }

    /// <summary>
    /// Combines multiple results, collecting all errors if any fail.
    /// Uses C# 14 collection expressions.
    /// </summary>
    /// <typeparam name="T">Value type.</typeparam>
    /// <param name="results">Results to combine.</param>
    /// <returns>Success with all values, or failure with aggregated errors.</returns>
    public static Result<IReadOnlyList<T>> CombineAll<T>(params Result<T>[] results)
    {
        ArgumentNullException.ThrowIfNull(results);

        List<T> values = [];
        List<Error> errors = [];

        foreach (var result in results)
        {
            if (result.IsSuccess)
            {
                values.Add(result.Value);
            }
            else
            {
                errors.Add(result.Error!);
            }
        }

        if (errors.Count > 0)
        {
            var aggregatedError = new Error(
                "MULTIPLE_ERRORS",
                $"Multiple operations failed: {string.Join(", ", errors.Select(e => e.Code))}");

            return Result<IReadOnlyList<T>>.Failure(aggregatedError);
        }

        return Result<IReadOnlyList<T>>.Success(values);
    }

    /// <summary>
    /// Converts an enumerable of results to a result of enumerable.
    /// Short-circuits on first failure.
    /// </summary>
    /// <typeparam name="T">Value type.</typeparam>
    /// <param name="results">Results to sequence.</param>
    /// <returns>Success with all values, or first failure.</returns>
    public static Result<IReadOnlyList<T>> Sequence<T>(this IEnumerable<Result<T>> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        List<T> values = [];

        foreach (var result in results)
        {
            if (result.IsFailure)
            {
                return Result<IReadOnlyList<T>>.Failure(result.Error!);
            }

            values.Add(result.Value);
        }

        return Result<IReadOnlyList<T>>.Success(values);
    }

    /// <summary>
    /// Applies a function to each element and sequences the results.
    /// </summary>
    /// <typeparam name="TSource">Source type.</typeparam>
    /// <typeparam name="TResult">Result type.</typeparam>
    /// <param name="source">Source elements.</param>
    /// <param name="selector">Function producing results.</param>
    /// <returns>Success with transformed values, or first failure.</returns>
    public static Result<IReadOnlyList<TResult>> Traverse<TSource, TResult>(
        this IEnumerable<TSource> source,
        Func<TSource, Result<TResult>> selector)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selector);

        return source.Select(selector).Sequence();
    }

    /// <summary>
    /// Partitions results into successes and failures.
    /// Uses C# 14 collection expressions and tuple deconstruction.
    /// </summary>
    /// <typeparam name="T">Value type.</typeparam>
    /// <param name="results">Results to partition.</param>
    /// <returns>Tuple of successful values and errors.</returns>
    public static (IReadOnlyList<T> Successes, IReadOnlyList<Error> Failures) Partition<T>(
        this IEnumerable<Result<T>> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        List<T> successes = [];
        List<Error> failures = [];

        foreach (var result in results)
        {
            if (result.IsSuccess)
            {
                successes.Add(result.Value);
            }
            else
            {
                failures.Add(result.Error!);
            }
        }

        return (successes, failures);
    }
}
