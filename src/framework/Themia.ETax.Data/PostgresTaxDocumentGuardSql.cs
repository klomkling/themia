using System.Text;

namespace Themia.ETax.Data;

/// <summary>
/// The PostgreSQL statements behind <see cref="TaxDocumentGuards"/>. Every statement is safe to run again on a database that already has the
/// objects (create-or-replace, drop-if-exists-then-create, create-if-not-exists, existence-checked constraint), so a migration that adopts an
/// existing schema, or runs twice, ends in the same state.
/// </summary>
internal static class PostgresTaxDocumentGuardSql
{
    private const string Delimiter = "$themia$";
    private const string ErrorCode = "integrity_constraint_violation";

    /// <summary>The trigger functions and triggers: immutable columns, no delete, no truncate, Cancelled is final, the replacement is write-once, Sent only leaves for a replacement.</summary>
    public static IReadOnlyList<string> Immutability(TaxDocumentTableMap map)
    {
        var table = map.QualifiedTable;
        var guardFn = map.QualifiedName(map.GuardFunctionName);
        var truncateFn = map.QualifiedName(map.TruncateFunctionName);
        var guardTrigger = TaxDocumentTableMap.Quote(map.GuardTriggerName);
        var truncateTrigger = TaxDocumentTableMap.Quote(map.TruncateTriggerName);
        var sent = TaxDocumentTableMap.Literal(map.SentStateValue, nameof(map.SentStateValue));
        var cancelled = TaxDocumentTableMap.Literal(map.CancelledStateValue, nameof(map.CancelledStateValue));
        var state = TaxDocumentTableMap.Quote(map.StateColumn);
        var replacedBy = TaxDocumentTableMap.Quote(map.ReplacedByColumn);
        var snapshot = TaxDocumentTableMap.Quote(map.SnapshotJsonColumn);
        var changed = string.Join(
            " OR ",
            map.ImmutableColumns.Select(c => $"NEW.{TaxDocumentTableMap.Quote(c)} IS DISTINCT FROM OLD.{TaxDocumentTableMap.Quote(c)}"));

        // The messages carry no row data: they can reach logs and users.
        var guardBody = new StringBuilder()
            .AppendLine($"CREATE OR REPLACE FUNCTION {guardFn}() RETURNS trigger LANGUAGE plpgsql AS {Delimiter}")
            .AppendLine("BEGIN")
            .AppendLine("  IF TG_OP = 'DELETE' THEN")
            .AppendLine($"    RAISE EXCEPTION 'Themia.ETax: issued tax documents cannot be deleted' USING ERRCODE = '{ErrorCode}';")
            .AppendLine("  END IF;")
            .AppendLine("  -- Guard on the OLD row: a row that had a snapshot stays guarded even if this UPDATE sets the snapshot to NULL.")
            .AppendLine($"  IF OLD.{snapshot} IS NULL THEN")
            .AppendLine("    RETURN NEW;   -- legacy row without a snapshot: untouched by Themia, may be updated")
            .AppendLine("  END IF;")
            .AppendLine($"  IF {changed} THEN")
            .AppendLine($"    RAISE EXCEPTION 'Themia.ETax: an issued tax document is immutable' USING ERRCODE = '{ErrorCode}';")
            .AppendLine("  END IF;")
            .AppendLine($"  IF OLD.{state} = {cancelled} AND NEW.{state} IS DISTINCT FROM OLD.{state} THEN")
            .AppendLine($"    RAISE EXCEPTION 'Themia.ETax: a cancelled tax document is final' USING ERRCODE = '{ErrorCode}';")
            .AppendLine("  END IF;")
            .AppendLine($"  IF OLD.{replacedBy} IS NOT NULL AND NEW.{replacedBy} IS DISTINCT FROM OLD.{replacedBy} THEN")
            .AppendLine($"    RAISE EXCEPTION 'Themia.ETax: the replacement of a tax document cannot be changed' USING ERRCODE = '{ErrorCode}';")
            .AppendLine("  END IF;")
            .AppendLine($"  IF OLD.{state} = {sent} AND NEW.{state} IS DISTINCT FROM OLD.{state}")
            .AppendLine($"     AND NOT (NEW.{state} = {cancelled} AND NEW.{replacedBy} IS NOT NULL) THEN")
            .AppendLine($"    RAISE EXCEPTION 'Themia.ETax: a sent tax document can only be cancelled by a replacement' USING ERRCODE = '{ErrorCode}';")
            .AppendLine("  END IF;")
            .AppendLine("  RETURN NEW;")
            .AppendLine("END;")
            .Append(Delimiter)
            .ToString();

        var truncateBody = new StringBuilder()
            .AppendLine($"CREATE OR REPLACE FUNCTION {truncateFn}() RETURNS trigger LANGUAGE plpgsql AS {Delimiter}")
            .AppendLine("BEGIN")
            .AppendLine($"  RAISE EXCEPTION 'Themia.ETax: issued tax documents cannot be truncated' USING ERRCODE = '{ErrorCode}';")
            .AppendLine("END;")
            .Append(Delimiter)
            .ToString();

        return
        [
            StateColumnCheck(map),
            guardBody,
            $"DROP TRIGGER IF EXISTS {guardTrigger} ON {table}",
            $"CREATE TRIGGER {guardTrigger} BEFORE UPDATE OR DELETE ON {table} FOR EACH ROW EXECUTE FUNCTION {guardFn}()",
            truncateBody,
            $"DROP TRIGGER IF EXISTS {truncateTrigger} ON {table}",
            $"CREATE TRIGGER {truncateTrigger} BEFORE TRUNCATE ON {table} FOR EACH STATEMENT EXECUTE FUNCTION {truncateFn}()",
        ];
    }

    /// <summary>
    /// Fails the migration, not the first UPDATE, when the state column cannot hold the configured state values: numbers need an integer column,
    /// strings need a text, character or native enum column. Without this a mismatch only shows up as a cast error on the first row that is updated.
    /// </summary>
    private static string StateColumnCheck(TaxDocumentTableMap map)
    {
        var table = TaxDocumentTableMap.Quote(map.Table);
        var regclass = (map.Schema is null ? table : TaxDocumentTableMap.Quote(map.Schema) + "." + table).Replace("'", "''", StringComparison.Ordinal);
        var fits = map.StateValuesAreNumeric
            ? "actual IN ('smallint', 'integer', 'bigint')"
            : "actual = 'text' OR actual LIKE 'character%' OR kind = 'e'";
        var expected = map.StateValuesAreNumeric ? "an integer column" : "a text, character or enum column";
        return new StringBuilder()
            .AppendLine($"DO {Delimiter}")
            .AppendLine("DECLARE actual text; kind \"char\";")
            .AppendLine("BEGIN")
            .AppendLine("  SELECT format_type(a.atttypid, a.atttypmod), t.typtype INTO actual, kind")
            .AppendLine("    FROM pg_attribute a JOIN pg_type t ON t.oid = a.atttypid")
            .AppendLine($"   WHERE a.attrelid = to_regclass('{regclass}') AND a.attname = '{map.StateColumn}' AND NOT a.attisdropped;")
            .AppendLine("  IF actual IS NULL THEN")
            .AppendLine($"    RAISE EXCEPTION 'Themia.ETax: state column {map.StateColumn} not found on {map.Table}';")
            .AppendLine("  END IF;")
            .AppendLine($"  IF NOT ({fits}) THEN")
            .AppendLine($"    RAISE EXCEPTION 'Themia.ETax: state column {map.StateColumn} is %, but the configured state values need {expected}', actual;")
            .AppendLine("  END IF;")
            .AppendLine("END")
            .Append(Delimiter)
            .ToString();
    }

    /// <summary>
    /// A composite foreign key (replaced-by, seller) to (id, seller) on the same table, so a replacement must belong to the replaced document's seller.
    /// DEFERRABLE INITIALLY DEFERRED because the replaced document is cancelled, pointing at its replacement, before the replacement is inserted.
    /// </summary>
    public static IReadOnlyList<string> SameSellerKey(TaxDocumentTableMap map)
    {
        var table = map.QualifiedTable;
        var id = TaxDocumentTableMap.Quote(map.IdColumn);
        var seller = TaxDocumentTableMap.Quote(map.SellerIdColumn);
        var replacedBy = TaxDocumentTableMap.Quote(map.ReplacedByColumn);
        var uniqueIndex = TaxDocumentTableMap.Quote(map.UniqueIndexName);
        var constraintName = map.ForeignKeyName;
        var tableRegclass = (map.Schema is null ? TaxDocumentTableMap.Quote(map.Table) : TaxDocumentTableMap.Quote(map.Schema) + "." + TaxDocumentTableMap.Quote(map.Table))
            .Replace("'", "''", StringComparison.Ordinal);

        var addForeignKey = new StringBuilder()
            .AppendLine($"DO {Delimiter}")
            .AppendLine("BEGIN")
            .AppendLine($"  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = '{constraintName}' AND conrelid = to_regclass('{tableRegclass}')) THEN")
            .AppendLine($"    ALTER TABLE {table} ADD CONSTRAINT {TaxDocumentTableMap.Quote(constraintName)}")
            .AppendLine($"      FOREIGN KEY ({replacedBy}, {seller}) REFERENCES {table} ({id}, {seller}) DEFERRABLE INITIALLY DEFERRED;")
            .AppendLine("  END IF;")
            .AppendLine("END")
            .Append(Delimiter)
            .ToString();

        return
        [
            // The referenced columns must be unique together; the primary key on id alone does not satisfy a composite foreign key.
            $"CREATE UNIQUE INDEX IF NOT EXISTS {uniqueIndex} ON {table} ({id}, {seller})",
            addForeignKey,
        ];
    }
}
