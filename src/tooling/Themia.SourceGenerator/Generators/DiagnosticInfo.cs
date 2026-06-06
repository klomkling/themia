using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Themia.SourceGenerator.Generators;

/// <summary>
/// Equatable, compilation-free representation of a diagnostic to emit. Carrying a live
/// <see cref="Location"/> through the incremental pipeline would root the compilation and break
/// caching, so we capture the file path + spans and rebuild the <see cref="Location"/> in the output stage.
/// </summary>
internal sealed record DiagnosticInfo(
    DiagnosticDescriptor Descriptor,
    string? FilePath,
    TextSpan Span,
    LinePositionSpan LineSpan,
    EquatableArray<string> MessageArgs) : IEquatable<DiagnosticInfo>
{
    public static DiagnosticInfo Create(DiagnosticDescriptor descriptor, Location location, params string[] messageArgs)
    {
        var lineSpan = location.GetLineSpan();
        return new DiagnosticInfo(
            descriptor,
            location.SourceTree?.FilePath,
            location.SourceSpan,
            lineSpan.Span,
            new EquatableArray<string>(messageArgs.ToImmutableArray()));
    }

    public Diagnostic ToDiagnostic()
    {
        var location = FilePath is null ? Location.None : Location.Create(FilePath, Span, LineSpan);
        // ImmutableArray<string> → object[] for the params messageArgs overload.
        var args = new object[MessageArgs.Length];
        var i = 0;
        foreach (var arg in MessageArgs)
            args[i++] = arg;
        return Diagnostic.Create(Descriptor, location, args);
    }
}

/// <summary>Value-equality wrapper over <see cref="ImmutableArray{T}"/> for use in incremental pipeline records.</summary>
internal readonly record struct EquatableArray<T>(ImmutableArray<T> Array) where T : IEquatable<T>
{
    public bool Equals(EquatableArray<T> other) => Array.AsSpan().SequenceEqual(other.Array.AsSpan());

    public override int GetHashCode()
    {
        // Manual FNV-style accumulate — System.HashCode is unavailable on netstandard2.0.
        unchecked
        {
            var hash = 17;
            foreach (var item in Array)
                hash = (hash * 31) ^ (item?.GetHashCode() ?? 0);
            return hash;
        }
    }

    public int Length => Array.Length;
    public ImmutableArray<T>.Enumerator GetEnumerator() => Array.GetEnumerator();
    public static EquatableArray<T> Empty => new(ImmutableArray<T>.Empty);
}
