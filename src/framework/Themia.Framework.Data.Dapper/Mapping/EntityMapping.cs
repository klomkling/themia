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

    private EntityMapping(
        string table,
        string keyColumn,
        string keyProperty,
        Dictionary<string, string> columns,
        Action<object, object?> keySetter,
        Type keyType)
    {
        Table = table;
        KeyColumn = keyColumn;
        KeyProperty = keyProperty;
        _columnByProperty = columns;
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
    public string Column(string propertyName) => _columnByProperty[propertyName];

    /// <summary>All property-to-column mappings.</summary>
    public IReadOnlyDictionary<string, string> Columns => _columnByProperty;

    /// <summary>Builds a convention-based mapping for <typeparamref name="T"/>.</summary>
    public static EntityMapping ForConvention<T>() => ForConvention(typeof(T));

    /// <summary>Builds a convention-based mapping for the given entity type.</summary>
    public static EntityMapping ForConvention(Type type)
    {
        var props = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead
                        && p.GetIndexParameters().Length == 0
                        && p.Name != "DomainEvents"
                        && p.Name != "IsTransient")
            .ToArray();

        var columns = props.ToDictionary(p => p.Name, p => ToSnakeCase(p.Name));

        var key = props.FirstOrDefault(p => p.Name == "Id")
                  ?? throw new InvalidOperationException(
                      $"Entity '{type.Name}' has no 'Id' property; provide an EntityMapping override.");

        var setter = BuildSetter(key);

        return new EntityMapping(
            Pluralize(ToSnakeCase(type.Name)),
            ToSnakeCase(key.Name),
            key.Name,
            columns,
            setter,
            key.PropertyType);
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
                        || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
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
