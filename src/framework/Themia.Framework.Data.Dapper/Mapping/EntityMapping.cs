using System.Reflection;
using System.Text;

namespace Themia.Framework.Data.Dapper.Mapping;

/// <summary>
/// Table/column name mapping for an entity type. Convention = snake_case columns, snake_case
/// pluralized table; the key property is "Id". Use <see cref="EntityMappingRegistry"/> to override.
/// </summary>
public sealed class EntityMapping
{
    private readonly Dictionary<string, string> _columnByProperty;
    private readonly Dictionary<string, PropertyInfo> _propertyByName;

    private EntityMapping(
        string table,
        string keyColumn,
        string keyProperty,
        Dictionary<string, string> columns,
        Dictionary<string, PropertyInfo> properties,
        Action<object, object?> keySetter,
        Type keyType)
    {
        Table = table;
        KeyColumn = keyColumn;
        KeyProperty = keyProperty;
        _columnByProperty = columns;
        _propertyByName = properties;
        KeySetter = keySetter;
        KeyType = keyType;
    }

    /// <summary>The (pluralized snake_case) table name.</summary>
    public string Table { get; }

    /// <summary>The key column name.</summary>
    public string KeyColumn { get; }

    /// <summary>The key property name (e.g. "Id").</summary>
    public string KeyProperty { get; }

    /// <summary>The CLR type of the key.</summary>
    public Type KeyType { get; }

    /// <summary>Writes the (possibly protected) key property — used to populate store-generated keys.</summary>
    public Action<object, object?> KeySetter { get; }

    /// <summary>Returns the column name for the given property name.</summary>
    public string Column(string propertyName) =>
        _columnByProperty.TryGetValue(propertyName, out var column)
            ? column
            : throw new InvalidOperationException(
                $"No column mapping for property '{propertyName}' on '{Table}'. Provide an EntityMapping override.");

    /// <summary>All property-to-column mappings.</summary>
    public IReadOnlyDictionary<string, string> Columns => _columnByProperty;

    /// <summary>Builds a convention-based mapping for <typeparamref name="T"/>.</summary>
    public static EntityMapping ForConvention<T>() => ForConvention(typeof(T), null, null);

    /// <summary>Builds a convention-based mapping for the given entity type.</summary>
    public static EntityMapping ForConvention(Type type) => ForConvention(type, null, null);

    /// <summary>
    /// Builds a convention-based mapping for <typeparamref name="T"/>, overriding the table name and individual
    /// column names (keyed by property name); pass null to keep the snake_case convention for that part.
    /// </summary>
    public static EntityMapping ForConvention<T>(string? table, IReadOnlyDictionary<string, string>? columnOverrides) =>
        ForConvention(typeof(T), table, columnOverrides);

    /// <summary>
    /// Builds a convention-based mapping for the given entity type, overriding the table name and individual
    /// column names (keyed by property name); pass null to keep the snake_case convention for that part.
    /// </summary>
    public static EntityMapping ForConvention(
        Type type,
        string? table,
        IReadOnlyDictionary<string, string>? columnOverrides)
    {
        var props = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead
                        && p.GetIndexParameters().Length == 0
                        && p.Name != "DomainEvents"
                        && p.Name != "IsTransient")
            .ToArray();

        var columns = props.ToDictionary(p => p.Name, p => ToSnakeCase(p.Name));
        var properties = props.ToDictionary(p => p.Name);

        if (columnOverrides is not null)
        {
            foreach (var (property, column) in columnOverrides)
            {
                if (!columns.ContainsKey(property))
                    throw new InvalidOperationException(
                        $"Column override for '{property}' does not match any mapped property on '{type.Name}'.");
                columns[property] = column;
            }
        }

        var key = props.FirstOrDefault(p => p.Name == "Id")
                  ?? throw new InvalidOperationException(
                      $"Entity '{type.Name}' has no 'Id' property; provide an EntityMapping override.");

        var setter = BuildSetter(key);

        return new EntityMapping(
            table ?? Pluralize(ToSnakeCase(type.Name)),
            columns[key.Name],   // respects a column override on the key property
            key.Name,
            columns,
            properties,
            setter,
            key.PropertyType);
    }

    /// <summary>Reads a mapped property via cached metadata (no per-call <c>GetProperty</c> lookup).</summary>
    internal object? GetValue(object entity, string property) =>
        _propertyByName.TryGetValue(property, out var pi)
            ? pi.GetValue(entity)
            : throw new InvalidOperationException($"No property '{property}' mapped on '{Table}'.");

    /// <summary>Tries to read a mapped property via cached metadata; returns false when it is not mapped.</summary>
    internal bool TryGetValue(object entity, string property, out object? value)
    {
        if (_propertyByName.TryGetValue(property, out var pi))
        {
            value = pi.GetValue(entity);
            return true;
        }
        value = null;
        return false;
    }

    // Best-effort: audit/soft-delete properties are settable on the concrete base entities
    // (AuditableEntity&lt;TId&gt;/SoftDeletableEntity&lt;TId&gt;); an unmapped or get-only property is silently skipped.
    /// <summary>Writes a mapped, writable property via cached metadata; unmapped or get-only properties are skipped.</summary>
    internal void SetValue(object entity, string property, object? value)
    {
        if (_propertyByName.TryGetValue(property, out var pi) && pi.CanWrite)
            pi.SetValue(entity, value);
    }

    private static Action<object, object?> BuildSetter(PropertyInfo key)
    {
        var setMethod = key.GetSetMethod(nonPublic: true)
                        ?? throw new InvalidOperationException(
                            $"Key property '{key.DeclaringType?.Name}.{key.Name}' has no setter.");
        return (entity, value) => setMethod.Invoke(entity, [value]);
    }

    /// <summary>Converts a PascalCase or camelCase name to snake_case.</summary>
    public static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0
                    && (char.IsLower(name[i - 1])
                        || char.IsDigit(name[i - 1])
                        || (char.IsUpper(name[i - 1])
                            && i + 1 < name.Length
                            && char.IsLower(name[i + 1]))))
                {
                    sb.Append('_');
                }
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static string Pluralize(string snake)
    {
        if (snake.EndsWith('y') && snake.Length > 1 && !"aeiou".Contains(snake[^2]))
            return string.Concat(snake.AsSpan(0, snake.Length - 1), "ies");
        if (snake.EndsWith('s') || snake.EndsWith("ch") || snake.EndsWith("sh") || snake.EndsWith('x'))
            return snake + "es";
        return snake + "s";
    }
}
