using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace Themia.Generators.Abstractions.Diagnostics;

/// <summary>
/// Factory for <see cref="DiagnosticDescriptor"/> instances with a consistent
/// shape (category, default-enabled, THEMIA ID format).
/// </summary>
public static class ThemiaDiagnostics
{
    private const string Category = "Themia.DI";
    private static readonly Regex IdPattern = new("^THEMIA[0-9]{3,}$", RegexOptions.Compiled);

    /// <summary>Creates an error-severity <see cref="DiagnosticDescriptor"/>.</summary>
    public static DiagnosticDescriptor CreateError(string id, string title, string messageFormat)
        => Create(id, title, messageFormat, DiagnosticSeverity.Error);

    /// <summary>Creates a warning-severity <see cref="DiagnosticDescriptor"/>.</summary>
    public static DiagnosticDescriptor CreateWarning(string id, string title, string messageFormat)
        => Create(id, title, messageFormat, DiagnosticSeverity.Warning);

    /// <summary>
    /// Creates a warning-severity <see cref="DiagnosticDescriptor"/> in a specific category, with an
    /// optional help link — for diagnostic families that need their own category (so adopters can
    /// configure them as a group) while keeping the shared ID validation and descriptor shape.
    /// </summary>
    public static DiagnosticDescriptor CreateWarning(string id, string title, string messageFormat, string category, string? helpLinkUri = null)
        => Create(id, title, messageFormat, DiagnosticSeverity.Warning, category, helpLinkUri);

    /// <summary>Creates an info-severity <see cref="DiagnosticDescriptor"/>.</summary>
    public static DiagnosticDescriptor CreateInfo(string id, string title, string messageFormat)
        => Create(id, title, messageFormat, DiagnosticSeverity.Info);

    private static DiagnosticDescriptor Create(
        string id, string title, string messageFormat, DiagnosticSeverity severity,
        string category = Category, string? helpLinkUri = null)
    {
        if (!IdPattern.IsMatch(id))
            throw new ArgumentException($"Diagnostic ID '{id}' must match /^THEMIA[0-9]{{3,}}$/.", nameof(id));

        return new DiagnosticDescriptor(
            id: id,
            title: title,
            messageFormat: messageFormat,
            category: category,
            defaultSeverity: severity,
            isEnabledByDefault: true,
            description: null,
            helpLinkUri: helpLinkUri);
    }
}
