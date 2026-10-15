using System.Globalization;
using System.Text.RegularExpressions;

namespace Themia.ETax.Data;

/// <summary>
/// Names the columns of an app's own tax document table, so the immutability trigger and the same-seller key can be created on
/// tables that do not follow Themia's naming (e.g. PascalCase). Identifiers are validated, then quoted.
/// </summary>
public sealed partial record TaxDocumentTableMap
{
    // PostgreSQL silently truncates identifiers to 63 bytes. A truncated name no longer matches the literal an existence check compares it
    // with, so the check never finds the object and a replay fails with "already exists". The longest name derived from the table is
    // "fk_<table>_replaced_same_seller" (24 characters of overhead), so the table itself is capped to keep every derived name within 63.
    internal const int MaxIdentifierLength = 63;
    private const int LongestDerivedNameOverhead = 24;
    internal const int MaxTableLength = MaxIdentifierLength - LongestDerivedNameOverhead;
    private const int MaxStateValueLength = 63;

    private static readonly string[] DefaultImmutableColumns =
    [
        "id", "kind", "source_event_id", "revision", "seller_id", "document_number", "issue_date",
        "snapshot_json", "snapshot_sha256", "snapshot_schema_version", "template_version",
    ];

    /// <summary>The table, unqualified. A table in the connection's <c>search_path</c> needs no schema.</summary>
    public required string Table { get; init; }

    /// <summary>
    /// The table's schema. Leave <see langword="null"/> (the default) to follow the connection's <c>search_path</c>, like the other Themia
    /// stores: nothing is created in a hardcoded <c>public</c>.
    /// </summary>
    public string? Schema { get; init; }

    /// <summary>The primary key column. Default <c>id</c>.</summary>
    public string IdColumn { get; init; } = "id";

    /// <summary>The seller column. Default <c>seller_id</c>.</summary>
    public string SellerIdColumn { get; init; } = "seller_id";

    /// <summary>The lifecycle state column. Default <c>state</c>.</summary>
    public string StateColumn { get; init; } = "state";

    /// <summary>The column holding the replacement's id on a cancelled document. Default <c>replaced_by_document_id</c>.</summary>
    public string ReplacedByColumn { get; init; } = "replaced_by_document_id";

    /// <summary>The snapshot text column. Rows whose snapshot is NULL are legacy rows and are not guarded against update. Default <c>snapshot_json</c>.</summary>
    public string SnapshotJsonColumn { get; init; } = "snapshot_json";

    /// <summary>
    /// Columns that may never change once a snapshot exists. Must contain <see cref="SnapshotJsonColumn"/>, or setting the snapshot back to NULL
    /// would not be caught. Default: id, kind, source_event_id, revision, seller_id, document_number, issue_date and the four snapshot columns.
    /// </summary>
    public IReadOnlyList<string> ImmutableColumns { get; init; } = DefaultImmutableColumns;

    /// <summary>The value stored in <see cref="StateColumn"/> for Sent: an integer, an enum (its NUMBER, so the column must be an integer type) or a plain string of letters, digits and underscores (a text column).</summary>
    public required object SentStateValue { get; init; }

    /// <summary>The value stored in <see cref="StateColumn"/> for Cancelled; the same kind as <see cref="SentStateValue"/>. The migration checks the column's type and fails if it does not fit.</summary>
    public required object CancelledStateValue { get; init; }

    internal void Validate()
    {
        CheckIdentifier(Table, nameof(Table), MaxTableLength);
        if (Schema is not null)
            CheckIdentifier(Schema, nameof(Schema), MaxIdentifierLength);
        foreach (var (name, value) in new[]
                 {
                     (nameof(IdColumn), IdColumn), (nameof(SellerIdColumn), SellerIdColumn), (nameof(StateColumn), StateColumn),
                     (nameof(ReplacedByColumn), ReplacedByColumn), (nameof(SnapshotJsonColumn), SnapshotJsonColumn),
                 })
        {
            CheckIdentifier(value, name, MaxIdentifierLength);
        }

        if (ImmutableColumns is not { Count: > 0 })
            throw new ArgumentException("ImmutableColumns must name at least the snapshot column.", nameof(ImmutableColumns));
        foreach (var column in ImmutableColumns)
            CheckIdentifier(column, nameof(ImmutableColumns), MaxIdentifierLength);
        if (!ImmutableColumns.Contains(SnapshotJsonColumn, StringComparer.Ordinal))
            throw new ArgumentException("ImmutableColumns must contain SnapshotJsonColumn, or clearing the snapshot would not be rejected.", nameof(ImmutableColumns));

        _ = Literal(SentStateValue, nameof(SentStateValue));
        _ = Literal(CancelledStateValue, nameof(CancelledStateValue));
        if ((SentStateValue is string) != (CancelledStateValue is string))
            throw new ArgumentException("Sent and Cancelled state values must both be numbers or both be strings.", nameof(CancelledStateValue));
        foreach (var name in GeneratedNames)
        {
            if (name.Length > MaxIdentifierLength)
                throw new ArgumentException($"A name derived from the table would exceed {MaxIdentifierLength} characters.", nameof(Table));
        }
    }

    internal string GuardFunctionName => $"{Table}_etax_guard";

    internal string TruncateFunctionName => $"{Table}_etax_no_truncate";

    internal string GuardTriggerName => $"trg_{Table}_etax_guard";

    internal string TruncateTriggerName => $"trg_{Table}_etax_no_truncate";

    internal string UniqueIndexName => $"ux_{Table}_id_seller";

    internal string ForeignKeyName => $"fk_{Table}_replaced_same_seller";

    /// <summary>Every object name derived from the table. Each must be at most 63 characters, or PostgreSQL truncates it silently.</summary>
    internal IReadOnlyList<string> GeneratedNames =>
        [GuardFunctionName, TruncateFunctionName, GuardTriggerName, TruncateTriggerName, UniqueIndexName, ForeignKeyName];

    /// <summary>True when the state values are numbers (the state column must then be an integer type), false for strings.</summary>
    internal bool StateValuesAreNumeric => SentStateValue is not string;

    internal static string Quote(string identifier) => "\"" + identifier + "\"";

    internal string QualifiedTable => Schema is null ? Quote(Table) : Quote(Schema) + "." + Quote(Table);

    internal string QualifiedName(string name) => Schema is null ? Quote(name) : Quote(Schema) + "." + Quote(name);

    // A state value becomes a SQL literal inside a dollar-quoted function body. Only numbers and plain word-like strings are accepted:
    // escaping quotes is not enough there, because a value containing the dollar-quote tag would end the body and let the rest run as SQL.
    internal static string Literal(object value, string paramName) => value switch
    {
        string s when StateWord().IsMatch(s) && s.Length <= MaxStateValueLength => "'" + s + "'",
        string => throw new ArgumentException(
            $"A string state value must be 1 to {MaxStateValueLength} letters, digits or underscores.", paramName),
        sbyte or byte or short or ushort or int or uint or long or ulong => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        Enum e => Convert.ToString(Convert.ChangeType(e, e.GetTypeCode(), CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)!,
        _ => throw new ArgumentException("A state value must be an integer, an enum (stored as its number) or a plain string.", paramName),
    };

    private static void CheckIdentifier(string value, string paramName, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxLength || !Identifier().IsMatch(value))
        {
            throw new ArgumentException(
                $"'{paramName}' must be a plain identifier (letters, digits, underscore; not starting with a digit; at most {maxLength} characters).",
                paramName);
        }
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex Identifier();

    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex StateWord();
}
