# Themia.ETax.Data

Database helpers for `Themia.ETax`. Apps own their tax document table and its migration; this package supplies the parts every app must get
right, so all of them get them the same way. PostgreSQL today. net10.0 (it needs the framework's transactional sequence allocator).

## The required guards (`TaxDocumentGuards`)

Call both from your own FluentMigrator migration, after the table exists:

```csharp
var map = new TaxDocumentTableMap { Table = "tax_documents", SentStateValue = 3, CancelledStateValue = 5 };
TaxDocumentGuards.ApplyImmutability(this, map);
TaxDocumentGuards.ApplySameSellerReplacementKey(this, map);
```

- **`ApplyImmutability`** creates a row trigger and a truncate trigger. Once a row has a snapshot, its key, seller, number, kind, issue date and
  the four snapshot columns can never change, *including by setting the snapshot to NULL* (the guard reads the OLD row). Nothing is ever deleted
  or truncated, legacy rows without a snapshot included. A Sent document can only become Cancelled together with a replacement id, a Cancelled document never changes state again, and
  once a replacement id is set it cannot be changed or cleared. Legacy rows
  (no snapshot) may still be updated, so an app can backfill them; they are guarded from the moment they gain a snapshot.
- **`ApplySameSellerReplacementKey`** adds a unique index on (id, seller) and a composite foreign key (replaced-by, seller) to (id, seller), so a
  document can only be replaced by one of the same seller. It is `DEFERRABLE INITIALLY DEFERRED` because `TaxDocumentService` cancels the old
  document, naming its replacement, before it inserts the replacement.
- **Names:** `TaxDocumentTableMap` takes your table and column names (PascalCase works); identifiers are validated, then quoted. The table name may be
  at most 39 characters, so every name derived from it (function, triggers, index, `fk_<table>_replaced_same_seller`) stays within PostgreSQL's 63;
  a longer one would be silently truncated and the replay check would never find it.
- **State values:** integers, enums (stored as their NUMBER, so the column must be an integer type) or plain strings of letters, digits and
  underscores (a text, character or native enum column). The migration checks the column's type and **fails at migration time** if it does not fit,
  rather than at the first UPDATE. Strings are limited to that alphabet because they are written into a function body.
- **Adoptable and repeatable:** functions are created-or-replaced, triggers dropped and recreated, the index and constraint are created only if
  missing, so a migration may adopt a database that already has some of it or run twice. An existing constraint of the same name is left as it is.
- **search_path:** objects are created unqualified (in the first schema of the connection's `search_path`) unless you set `Schema`. Nothing is
  hardcoded to `public`.
- **Other engines:** the migration throws `NotSupportedException` instead of silently leaving issued tax documents editable.
- **Run the application as a role that does NOT own the table.** The trigger stops application bugs, not someone with DDL rights: the table's
  owner (or a superuser) can `ALTER TABLE ... DISABLE TRIGGER` or drop it. Let the migration role own the table and the guards, and give the
  application role only row access, so disabling the trigger needs the migration credentials:

  ```sql
  -- run by the migration role (the owner of tax_documents):
  GRANT SELECT, INSERT, UPDATE ON tax_documents TO app_role;     -- no DELETE, TRUNCATE, TRIGGER or REFERENCES
  ```

  Row guards still apply to the application role; `ALTER TABLE` and `DROP` are refused to it because it is not the owner. Roles with
  `session_replication_role = replica` (superuser) also bypass triggers, so never connect the application as a superuser.

## Document numbers (`SequenceTaxDocumentNumberSource`)

`services.AddThemiaETaxSequenceNumbers()` (after `AddThemiaSequences`) registers an `ITaxDocumentNumberSource` over
`ITransactionalSequenceAllocator`: the number is drawn inside the issue transaction, so commit keeps it and rollback returns it. One run per seller,
kind and (by default) year, formatted `TAX-2026-000001`. Prefixes, period, padding and tenant-or-host counters are options; anything else is your own
`ITaxDocumentNumberSource`. Seller ids must be short enough for the allocator's 100-character key. The caller's transaction must be READ COMMITTED.
