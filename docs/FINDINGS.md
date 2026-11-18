# DbToEntity: Analysis Findings & Roadmap

Written from a full code review on 2026-11-18. Each item points to the file it concerns. Update **Status** as items are done.

Status legend: ✅ fixed · 🔲 open

---

## 1. Bugs

### 1.1 ✅ Navigation names differed between entities and `DbContext`
- **Where:** `RoslynEntityGenerator.cs` (`GenerateEntity` vs `GenerateDbContext`)
- **Symptom:** the generated `DbContext` didn't compile when a table referenced itself (`d.Category`, but the entity had `CategoryNav`) or had several FKs to the same table (`d.UploadedFile`, but the entity had `PhotoFile` / `SignFile`). `.WithMany()` with no argument also disagreed with the `[InverseProperty]` attributes, so EF could create extra relationships.
- **Fix:** all names now come from `EntityNaming.cs` (`GetPropertyName`, `ResolveNavigationNames`), and the results are stored on `ForeignKeyMetadata.NavigationName` / `InverseNavigationName`. The fluent config emits `HasOne(d => d.Nav).WithMany(p => p.Inverse)`. `HasKey` now also applies the `Member` suffix.

### 1.1b ✅ Generated entities missing `using System.Collections.Generic`
- **Where:** `RoslynEntityGenerator.GenerateEntity`
- `ICollection<>` navigations only compiled in projects with `ImplicitUsings` turned on. The new test project caught this. Fixed by emitting the using.

### 1.2 ✅ `update` loses relationships and puts files in the wrong place
- **Where:** `Program.cs` `RunUpdate`, `PostgresMetadataProvider.cs:81`
- The provider loads only the named tables and then throws away FKs whose target isn't loaded. So `update --tables users` removes every navigation to tables you didn't list, and every collection pointing back.
- With `--separate-by-schema`, `update` leaves out the `Tables/`/`Views/` type folder that `generate` uses. The file lands somewhere else, leaving two copies of the class.
- `UpdateDbContext` only adds missing `DbSet`s. `OnModelCreating` is never updated, so new columns and FKs get no fluent config.
- `update` works out duplicate class names without `.Replace("_","")`, unlike `generate`.
- **Fixed:**
  - The table set is the tables the existing `DbContext` maps (`IncrementalUpdate.ReadMappedTableNames`) plus the requested ones. Metadata for all of them is loaded, so relationships are kept and names are computed over the same set as `generate`.
  - Requested tables are regenerated together with related tables, both now (FKs either way) and before (navigation types in the old entity file), so adding or dropping an FK updates both sides.
  - `UpdateDbContext` replaces only those tables' `modelBuilder.Entity<T>` blocks, inserting new ones before `OnModelCreatingPartial`, and adds missing `DbSet`s and `using`s. It also refreshes `ConfigureNpgsql`. Other blocks are untouched, because their entity files may not match the current database.
  - Naming and folder layout live in `ModelPipeline` (Core) and are shared by both commands and the tests.
- **Not handled:** tables dropped from the database stay in the `DbContext`. Renamed tables appear as new ones.
- Also fixed: generated files had mixed line endings (LF header and doc comments, CRLF body). Everything is CRLF now, and a test checks it.

### 1.3 ✅ SQL injection in the `--tables` filter
- **Where:** `PostgresMetadataProvider.cs:33-38`
- Table names are pasted into the SQL, so a name containing `'` breaks the query.
- **Fixed:** `AND c.relname = ANY(@names)` with a `text[]` parameter, built in `PostgresMetadataProvider.CreateTableQuery`. Tested by `MetadataQueryTests` (command text and parameters; no database needed).

### 1.4 🔲 Gaps in reading the schema
- ✅ ~~Materialized views get no columns~~: columns are now read from `pg_attribute` (domains resolve to their base type; lengths and defaults match what `information_schema` gave). Found and verified by the integration tests.
- ✅ ~~Partitioned tables get no indexes~~: the index query now includes `relkind` `'p'` and `'m'`.
- ✅ ~~Composite index column order isn't guaranteed~~: ordered by position in `indkey`.
- Expression indexes are silently dropped, and partial indexes are written as full indexes (no `HasFilter`). `IndexMetadata.IsPartial`/`HasExpressions` are now read, but only used for one-to-one detection.
- ✅ ~~FKs that reference a unique non-PK column get no `HasPrincipalKey`~~: EF threw at model build ("cannot target the primary key … not compatible"), so the app crashed at startup. The generator now emits `HasPrincipalKey(...)` (or `HasPrincipalKey<T>` for one-to-one) whenever the target columns aren't the principal's PK in the same order, and keeps the `confkey` column pairing.
  - **Limitation:** if the referenced unique column is nullable, EF makes the alternate key required, so a principal row with NULL there can't be saved through EF. EF can't model this; it's covered by a test.
- **FK target matching ignores schema** (`PostgresMetadataProvider.cs:77-89`, name-only `HashSet` / `FirstOrDefault`). Two schemas with the same table name can attach FKs to the wrong table.

### 1.5 ✅ Unknown Postgres types became `object`
- **Where:** `TypeMapper.cs`
- EF can't map `object`, so the app failed when building the model. This affected arrays (`_int4`…), enums, `time`, `interval`, `inet`, `cidr`, `citext`, `xml`, `point`, etc.
- **Fixed with F1.** Unsupported types (e.g. PostGIS `geometry`) are now skipped with a warning, along with the PKs, indexes and FKs that use them.

### 1.5b ✅ `timestamp`, `json`/`jsonb`, `money`, `xml`, `citext`, `oid`/`xid` were written as the wrong Postgres type
- `DateTime` defaults to `timestamptz` in Npgsql 6+, so writes to `timestamp` columns failed with "Cannot write DateTime with Kind=Unspecified…".
- `string` defaults to `text`, so inserts into `jsonb`/`json`/`xml` columns were rejected.
- **Fix:** `TypeMapper` emits `HasColumnType(...)` wherever Npgsql's default store type differs. `TypeMappingTests` checks every mapping against the store type EF actually reports.

### 1.5c ✅ Class names weren't valid identifiers
- Found by the integration tests: a table named `it's` produced `class It's`, so the whole project failed to compile. The same applied to names with `-`, spaces, or a leading digit (`2fa_codes`).
- **Fix:** class names (and fallback FK class names) go through `SanitizeIdentifier`, keeping the `_` prefix for leading digits.

### 1.5d ✅ `--separate-by-schema` broke builds with cross-schema relationships or namespace-named types
- Entities in different schema namespaces never imported each other, so any cross-schema FK (e.g. `sales.orders → public.users`) failed to compile. **Fix:** `ModelPipeline.AssignLocations` sets every table, enum and FK namespace before generation, and entity files import related entities' and enums' namespaces.
- Found on a real ~500-table database: schema `message_queue` + table `message_queues` → class `MessageQueue`, shadowed by namespace `…MessageQueue` (CS0118). **Fix:** `ModelPipeline.ReservedNames` (root namespace parts; with separate-by-schema also schema folders and `Tables`/`Views`/`MaterializedViews`/`Enums`) → colliding classes get an `Entity` suffix, enums an `Enum` suffix, with a note; a `classNames` override to a reserved name is an error.
- Enums now go to `<Schema>/Enums/` with a matching namespace (previously the output root). Old generated copies at the root are removed when moved.

### 1.5e ✅ Schema names were used unsanitized as folder and namespace names (security review)
- Postgres allows almost anything in a quoted schema name. With `--separate-by-schema` the name only went through `Pascalize()`, so a schema like `/evil` produced a rooted path. `Path.Combine` then wrote files outside the output folder (e.g. the drive root), and the enum move could delete a generated file there. Odd but legitimate names (`my-schema`, `2024_data`) produced namespaces that didn't compile. The clash prefix for class names (`<Schema><Class>`) had the same problem.
- **Fix:** `EntityNaming.SchemaSegment` (only `[A-Za-z0-9]`, no leading digit) is used for every schema-derived folder, namespace, reserved name and class prefix. As a second layer, the CLI refuses to write or delete outside the output folder (`InsideOutput`). The `SanitizeIdentifier` fallback is now deterministic (it was a random GUID). Verified with a real `/evil` schema: the files stay inside and compile.
- Npgsql itself can't load a database with a type in a schema whose name contains a dot (`"../../evil".kind` → "Given value does not contain a schema"). That fails before generation and writes nothing.

### 1.6 🔲 Smaller issues
- ✅ ~~`SanitizeIdentifier` makes up a random name when a name has no valid characters~~: now deterministic (1.5e).
- ✅ ~~Command handlers exit with code 0 on errors / raw stack traces~~ (Q5).
- ✅ ~~Views got a `[Table]` attribute~~: EF mapped them to a table *and* the view, so migrations would create a table per view. Found by the fluent-only equivalence test.
- ✅ ~~`update` must be run with the same naming options as `generate`~~: `generate` saves them to `db2entity.json` (F7).
- ✅ ~~`GetNamespace` treats an explicit `--namespace GeneratedEntities` as "not provided"~~ (null now means "not given").
- A `.csproj` name with `-` produces an invalid namespace.

---

## 2. Missing features (prioritized)

### Correctness (generated code is wrong at runtime)
| # | Feature | Notes |
|---|---------|-------|
| F1 ✅ | **Type mapping**: enums, arrays, `time`/`interval`/`inet`/`citext` | Done: 47 built-in types + arrays. Enums become C# enums with `[PgName]` and a generated `AppDbContext.ConfigureNpgsql` helper (EF 9+ `MapEnum`). Open: `numeric(p,s)` precision, multiranges, PostGIS/NodaTime plugins, domains. `update` refreshes enum files but cannot add the helper to an existing DbContext. |
| F2 ✅ | **Delete rules**: `OnDelete(DeleteBehavior.X)` | Done. `confdeltype` is mapped: CASCADE→Cascade, SET NULL→SetNull, RESTRICT→Restrict, NO ACTION→ClientSetNull (as EF's scaffolder does), SET DEFAULT→NoAction. Always emitted explicitly. Before this, required FKs got EF's `Cascade` convention even when the database said NO ACTION. `ON UPDATE` is ignored (EF has no equivalent). |
| F3 ✅ | **One-to-one detection** | Done. FK columns that (as a set) equal the PK or a unique index that is neither partial nor has expressions → `WithOne(...).HasForeignKey<TDependent>(...)` and a singular reference inverse (`User.UserProfile`). |
| F4 | **Generated / identity columns** | `attgenerated = 's'` → `HasComputedColumnSql(..., stored: true)`. `attidentity` → `UseIdentityAlwaysColumn()` / `UseIdentityByDefaultColumn()`. |

### Usability (needed for routine use on real projects)
| # | Feature | Notes |
|---|---------|-------|
| F5 ✅ | **Customizations survive regeneration** | Done. The `DbContext` is `partial` and calls `partial void OnModelCreatingPartial(ModelBuilder)` **last**, so user configuration overrides generated configuration. Every generated file has an `// <auto-generated>` header. Open (optional): one `IEntityTypeConfiguration<T>` per entity. |
| F6 ✅ | **Output that works with nullable reference types** | Done. Files start with `#nullable enable` after the header. Nullable reference columns → `T?`; NOT NULL ones and required navigations → `= null!`; optional FK navigations and one-to-one inverses → `T?`; collections → `= new List<T>()`; DbSets → `= null!`. Tests check the annotations by reflection, that EF's requiredness still matches the database in both styles, and that there are zero warnings with the project's nullable setting Enable/Disable/Warnings (and after updating an older DbContext). |
| F7 ✅ | **Config file `db2entity.json`** | Done. Command line > file > defaults; `generate` saves the resolved settings (never a `--connection` value), so `update` reuses them. `excludeTables` globs (FKs to excluded tables dropped; `__EFMigrationsHistory` always excluded; `--tables` wins), `classNames` overrides (validated; clashes are errors), `connectionStringName`. Unknown keys are errors. |

### Quick wins
| # | Feature | Notes |
|---|---------|-------|
| Q1 ✅ | `--context <Name>` | Done; validated as a C# identifier. |
| Q2 ✅ | `--no-pluralize` | Done: class names keep the table name; DbSets and collections aren't pluralized. DbSet names that would hide `DbContext` members (`Database`, `Model`, …) get a `Set` suffix. |
| Q3 ✅ | `--fluent-only` | Done: plain entity classes, all mapping fluent (indexes via `HasIndex`). A test proves both styles produce an identical EF model. No annotations-only mode: attributes can't express delete behaviour, principal keys, defaults or column types. |
| Q4 ✅ | Comments | Done: table/column comments → `/// <summary>` (XML-escaped) and `HasComment(...)` (tables via `ToTable(..., tb => tb.HasComment(...))`; views get doc comments only). |
| Q5 ✅ | Exit codes | Done: exit 1 with a one-line message on any failure (stack trace only with `DB2ENTITY_DEBUG=1`). Checked by running the real CLI. |

---

## 3. Upkeep and technical debt
- ✅ **Tests:** `DbToEntity.Tests` (142 tests) covers name resolution, generate → compile → EF model, and **integration tests against a real PostgreSQL 17** (Testcontainers): every metadata query, plus real writes and reads through the generated `DbContext`. They also prove the `timestamp`/`jsonb` column-type fixes (removing them makes writes fail with Npgsql's real errors). Integration tests are skipped when Docker isn't running. Not covered: the CLI's file I/O (exercised manually).
- **N+1 queries:** 4 round-trips per table. Load each kind of metadata in one query for all tables.
- `System.CommandLine 2.0.0-beta4` is an old preview. The stable 2.x API is different.
- ✅ Naming and folder-layout logic moved from `Program.cs` to Core (`ModelPipeline`, `IncrementalUpdate`). The CLI is now mostly I/O.
- Leftover code: unused `ToPascalCase`. ✅ ~~versions don't match (Core 1.0.6 vs CLI 1.2.9), Core `<Authors>` set to `DbToEntity`~~: both are 2.0.0 now, with the same author.

---

## 4. Suggested order
1. ✅ Navigation naming (1.1)
2. ✅ Test project, a safety net for everything below
3. ✅ F1 type mapping → ✅ F2 delete rules → ✅ F3 one-to-one
4. ✅ F5 `OnModelCreatingPartial`
5. ✅ Fix `update` (1.2), ✅ the SQL parameter (1.3), schema-reading gaps (1.4)
6. ✅ Quick wins Q1–Q5, ✅ F7, ✅ F6
