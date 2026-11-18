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

### 1.2 🔲 `update` loses relationships and puts files in the wrong place
- **Where:** `Program.cs` `RunUpdate`, `PostgresMetadataProvider.cs:81`
- The provider loads only the named tables and then throws away FKs whose target isn't loaded. So `update --tables users` removes every navigation to tables you didn't list, and every collection pointing back.
- With `--separate-by-schema`, `update` leaves out the `Tables/`/`Views/` type folder that `generate` uses. The file lands somewhere else, leaving two copies of the class.
- `UpdateDbContext` only adds missing `DbSet`s. `OnModelCreating` is never updated, so new columns and FKs get no fluent config.
- `update` works out duplicate class names without `.Replace("_","")`, unlike `generate`.
- *Partly addressed:* `update` now fills in FK class names and navigation names (`ResolveForeignKeyNames`).
- **Suggested fix:** load the whole schema's metadata for naming and relationships, and write files only for the requested tables. Move the folder/namespace logic into one helper shared by both commands.

### 1.3 🔲 SQL injection in the `--tables` filter
- **Where:** `PostgresMetadataProvider.cs:33-38`
- Table names are pasted into the SQL, so a name containing `'` breaks the query.
- **Fix:** `AND c.relname = ANY(@names)` with a `text[]` parameter.

### 1.4 🔲 Gaps in reading the schema
- **Materialized views get no columns.** `information_schema.columns` doesn't list them. Read from `pg_attribute` instead (`attnum > 0 AND NOT attisdropped`).
- **Partitioned tables get no indexes.** The index query filters on `t.relkind = 'r'`. Allow `'p'` and `'m'` too.
- **Composite index column order isn't guaranteed.** `array_agg(a.attname)` needs `ORDER BY array_position(ix.indkey, a.attnum)`.
- Expression indexes are silently dropped, and partial indexes are written as full indexes (no `HasFilter`).
- **FK target matching ignores schema** (`PostgresMetadataProvider.cs:77-89`, name-only `HashSet` / `FirstOrDefault`). Two schemas with the same table name can attach FKs to the wrong table.

### 1.5 ✅ Unknown Postgres types became `object`
- **Where:** `TypeMapper.cs`
- EF can't map `object`, so the app failed when building the model. This affected arrays (`_int4`…), enums, `time`, `interval`, `inet`, `cidr`, `citext`, `xml`, `point`, etc.
- **Fixed with F1.** Unsupported types (e.g. PostGIS `geometry`) are now skipped with a warning, along with the PKs, indexes and FKs that use them.

### 1.5b ✅ `timestamp`, `json`/`jsonb`, `money`, `xml`, `citext`, `oid`/`xid` were written as the wrong Postgres type
- `DateTime` defaults to `timestamptz` in Npgsql 6+, so writes to `timestamp` columns failed with "Cannot write DateTime with Kind=Unspecified…".
- `string` defaults to `text`, so inserts into `jsonb`/`json`/`xml` columns were rejected.
- **Fix:** `TypeMapper` emits `HasColumnType(...)` wherever Npgsql's default store type differs. `TypeMappingTests` checks every mapping against the store type EF actually reports.

### 1.6 🔲 Smaller issues
- `SanitizeIdentifier` makes up a random name (`Property_xxxx`) when a column name has no valid characters. It isn't deterministic, so two calls give different names.
- Command handlers print `Error:` but still exit with code 0, and a failed connection shows a raw stack trace.
- `GetNamespace` treats an explicit `--namespace GeneratedEntities` as "not provided".
- A `.csproj` name with `-` produces an invalid namespace.

---

## 2. Missing features (prioritized)

### Correctness (generated code is wrong at runtime)
| # | Feature | Notes |
|---|---------|-------|
| F1 ✅ | **Type mapping**: enums, arrays, `time`/`interval`/`inet`/`citext` | Done: 47 built-in types + arrays. Enums become C# enums with `[PgName]` and a generated `AppDbContext.ConfigureNpgsql` helper (EF 9+ `MapEnum`). Open: `numeric(p,s)` precision, multiranges, PostGIS/NodaTime plugins, domains. `update` refreshes enum files but cannot add the helper to an existing DbContext. |
| F2 | **Delete rules**: `OnDelete(DeleteBehavior.X)` | Read `pg_constraint.confdeltype` (`a`/`r`/`c`/`n`/`d`). Today EF's defaults may disagree with the database. |
| F3 | **One-to-one detection** | FK columns covered by a unique index/constraint → `HasOne().WithOne()` and a single reference instead of a collection. |
| F4 | **Generated / identity columns** | `attgenerated = 's'` → `HasComputedColumnSql(..., stored: true)`. `attidentity` → `UseIdentityAlwaysColumn()` / `UseIdentityByDefaultColumn()`. |

### Usability (needed for routine use on real projects)
| # | Feature | Notes |
|---|---------|-------|
| F5 | **Customizations survive regeneration** | Emit `partial void OnModelCreatingPartial(ModelBuilder)`, the same pattern EF's own scaffolder uses. Optionally emit one `IEntityTypeConfiguration<T>` per entity. |
| F6 | **Output that works with nullable reference types** | `string?` for nullable text, `= null!` for required references, `= new List<T>()` for collections. Detect `<Nullable>` from the `.csproj`. |
| F7 | **Config file `db2entity.json`** | Save connection, schema, output, namespace, include/exclude patterns, and naming overrides (`tbl_usr` → `User`). |

### Quick wins
| # | Feature | Notes |
|---|---------|-------|
| Q1 | `--context <Name>` | `GeneratorConfig.DbContextName` already exists but the command line never sets it. |
| Q2 | `--no-pluralize` | For tables that are already singular, or not in English. |
| Q3 | `--annotations` / `--fluent-only` | Today both styles are emitted, repeating every setting. |
| Q4 | Comments | `obj_description` / `col_description` → `HasComment()` + `/// <summary>`. |
| Q5 | Exit codes | Non-zero on failure, so CI and scripts can detect it. |

---

## 3. Upkeep and technical debt
- ✅ **Tests added:** `DbToEntity.Tests` (23 tests) covers name resolution and generate → compile → EF model. Not covered yet: `PostgresMetadataProvider` (needs a real Postgres, e.g. Testcontainers) and the CLI commands.
- **N+1 queries:** 4 round-trips per table. Load each kind of metadata in one query for all tables.
- `System.CommandLine 2.0.0-beta4` is an old preview. The stable 2.x API is different.
- `Program.cs` mixes command wiring with naming and folder-layout logic. That logic should live in Core so it can be tested.
- Leftover code: unused `ToPascalCase`, versions that don't match (Core 1.0.6 vs CLI 1.2.9), Core `<Authors>` set to `DbToEntity`.

---

## 4. Suggested order
1. ✅ Navigation naming (1.1)
2. ✅ Test project, a safety net for everything below
3. ✅ F1 type mapping → F2 delete rules → F3 one-to-one
4. F5 `OnModelCreatingPartial`
5. Fix `update` (1.2), the SQL parameter (1.3), schema-reading gaps (1.4)
6. Quick wins Q1–Q5, then F6/F7
