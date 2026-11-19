# DbToEntity

**A Smart EF Core Entity Generator for PostgreSQL**

`DbToEntity` is a CLI tool designed to generate Entity Framework Core entities from an existing PostgreSQL database. It distinguishes itself with:
-   **Partitioning Awareness**: Automatically detects partitioned tables and generates entities only for the parent table.
-   **Incremental Updates**: The `update` command allows you to regenerate specific entities non-destructively.
-   **Project Awareness**: Automatically detects your connection string from `appsettings.json` and namespace from your `.csproj` file.
-   **Fluent API**: Generates complete `OnModelCreating` configurations (Keys, Constraints, Relationships, Defaults) rather than just attributes.

---

---

## Installation

### 1. Install Global Tool (Standard)
Install explicitly from NuGet.org:

```powershell
dotnet tool install --global MH.DbToEntity.CLI
```

### 2. Update Tool
To get the latest version later:

```powershell
dotnet tool update --global MH.DbToEntity.CLI
```

---

## Upgrading to 2.0

2.0 fixes code that didn't compile or mapped the database incorrectly. Regenerating will change most generated files. Review the diff, and check that:

-   **Your edits live in your own files.** Generated files are overwritten. Move custom code to partial classes, and DbContext configuration to `OnModelCreatingPartial` (see *Customizing Without Losing Changes*).
-   **Enums are registered.** If your tables use PostgreSQL enums, pass the generated helper: `options.UseNpgsql(connectionString, AppDbContext.ConfigureNpgsql)`. This requires **EF Core 9 or later**.
-   **Delete behaviour matches the database.** Relationships now get an explicit `OnDelete(...)` from each foreign key's `ON DELETE` action. Before, EF assumed cascade delete for every required relationship.
-   **Nullability is annotated.** Generated files use `#nullable enable` (`string?`, `= null!`), so code that reads entities may get new nullable warnings in projects with `<Nullable>enable</Nullable>`.
-   **Views aren't mapped as tables.** Views no longer get `[Table]`, which made EF also map them to a table (and made migrations try to create one).
-   **`db2entity.json` is new.** `generate` now writes it next to your project so `update` reuses the same settings. Commit it; it never contains a `--connection` value.

## Troubleshooting

### Error: "Package type 'DotnetTool' is not supported"
If you see this, you likely tried to "Manage NuGet Packages" and add it to a project. **Do not do that.**
This is a CLI tool. Install it on your machine using the console commands above.
Then run it using `db2entity generate`.

## Quick Start

If you are running this inside a .NET project that already has an **`appsettings.json`** with a connection string:

```powershell
db2entity generate
```

**That's it!** The tool will:
1.  Read `ConnectionStrings:DefaultConnection` (or `Postgres`) from your `appsettings.json`.
2.  Detect your project's namespace from the `.csproj` file in the current directory.
3.  Generate entities in `./Entities` and your `DbContext`.

---

## Detailed Usage

### 1. Generate Entities (Initial Scaffold)

If you need to override defaults or run outside a project:

```powershell
db2entity generate --connection "Host=localhost;Database=mydb;..." --schema "public" --output "./Data/Entities" --namespace "MyApp.Domain"
```

**Options:**
-   `--connection`: (Optional) PostgreSQL connection string. Defaults to `appsettings.json`.
-   `--schema`: (Optional) Schema to target (default: all schemas).
-   `--output`: (Optional) Output directory (default: `./Entities`).
-   `--namespace`: (Optional) Namespace (default: detected from `.csproj`).
-   `--separate-by-schema`: (Optional) Organize into subfolders by schema (default: false).
-   `--tables`: (Optional) Generate only specific tables (space separated).
-   `--context`: (Optional) DbContext class name (default: `AppDbContext`).
-   `--no-pluralize`: (Optional) Keep table names as they are: `users` → class `Users`, DbSet `Users` (default: `User` / `Users`).
-   `--fluent-only`: (Optional) Plain entity classes without attributes; all mapping (including indexes) is in the DbContext.

The command exits with code `1` on any error (bad connection, invalid options, no tables found), so it is safe to use in scripts and CI.

### 2. Update Specific Tables (Incremental)

When you change specific tables (e.g., add a column to `users`), use `update` to refresh them without regenerating everything:

-   Relationships to the other tables already in your `DbContext` are kept.
-   Related tables are regenerated too when their navigation properties depend on the change (for example, after adding or dropping a foreign key). The tool lists them.
-   In the `DbContext`, only the configuration blocks of regenerated tables are replaced; missing `DbSet`s are added. Everything else stays as it was.
-   To add a new table, pass it in `--tables`.

```powershell
db2entity update --tables "users"
```

**Options:**
-   `--tables`: (Required) List of tables to update.
-   All other options from `generate` apply. You normally don't need them: `update` reads the settings `generate` saved in `db2entity.json`, so names and file locations match.

---

## Features

### Fluent API Generation
Instead of cluttering your classes with attributes, `DbToEntity` generates a clean `OnModelCreating` method:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.Entity<User>(entity =>
    {
        entity.ToTable("users", "public");
        entity.HasKey(e => e.Id).HasName("users_pkey");
        entity.Property(e => e.Email).HasColumnName("email").IsRequired().HasMaxLength(255);
        entity.HasOne(d => d.Role).WithMany(p => p.Users).HasForeignKey(d => d.RoleId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_users_role");
    });
}
```

### Schema-based Folder Organization
Use `--separate-by-schema` to organize generated code by database schema and kind. Folders and namespaces match:

```
Entities/
  AppDbContext.cs                      MyApp.Domain
  Tables/User.cs                       MyApp.Domain.Tables            (public schema: no schema folder)
  Views/ActiveUser.cs                  MyApp.Domain.Views
  Enums/Mood.cs                        MyApp.Domain.Enums
  Sales/Tables/Order.cs                MyApp.Domain.Sales.Tables
  Sales/Enums/OrderStatus.cs           MyApp.Domain.Sales.Enums
  Logs/MaterializedViews/DailyStat.cs  MyApp.Domain.Logs.MaterializedViews
```

-   Generated files import the namespaces they need, including entities and enums from other schemas.
-   A class or enum that would have the same name as one of these namespaces (e.g. table `message_queue.message_queues` → `MessageQueue`, also the schema's namespace) is renamed with an `Entity`/`Enum` suffix, and the tool tells you. Use `classNames` in `db2entity.json` to pick another name.
-   Enum files generated earlier at the root of the output folder are moved automatically.

### Intelligent Naming
Uses `Humanizer` to ensure C# conventions:
-   Table `users` -> Class `User`
-   Column `first_name` -> Property `FirstName`
-   Foreign Key `role_id` -> Navigation `Role`

### Settings File (`db2entity.json`)
`generate` saves the settings it used to `db2entity.json` in the current directory, and both commands read it. You can also write or edit it by hand:

```jsonc
{
  "connectionStringName": "DefaultConnection",   // looked up in appsettings.json
  "schema": "public",
  "output": "./Data/Entities",
  "namespace": "MyApp.Data",
  "context": "ShopContext",
  "separateBySchema": false,
  "pluralize": true,
  "fluentOnly": false,
  "excludeTables": ["audit_*", "legacy.*"],       // globs on "table" or "schema.table"
  "classNames": { "tbl_usr": "User", "sales.orders": "SalesOrder" }
}
```

-   **Precedence:** command-line options, then `db2entity.json`, then built-in defaults. Use `--config <path>` to use a different file.
-   Comments and trailing commas are allowed. Unknown settings are reported as errors, so typos don't go unnoticed.
-   **Connection strings passed with `--connection` are never saved** (they usually contain a password). Use `connectionStringName`, or add `"connection"` to the file yourself.
-   EF Core's `__EFMigrationsHistory` table is always excluded. Tables named in `--tables` are generated even if an exclude pattern matches them.

### Nullable Reference Types
Generated files use `#nullable enable` and follow the database, so callers see accurate nullability whatever your project's `<Nullable>` setting (no warnings either way):

```csharp
public string Email { get; set; } = null!;          // NOT NULL
public string? Nickname { get; set; }               // nullable
public virtual Role Role { get; set; } = null!;     // required foreign key
public virtual User? Manager { get; set; }          // optional foreign key
public virtual ICollection<Order> Orders { get; set; } = new List<Order>();
```

### Database Comments
`COMMENT ON TABLE` / `COMMENT ON COLUMN` text becomes XML doc comments (`/// <summary>`) on the generated classes and properties, plus `HasComment(...)` in the fluent configuration, so IntelliSense shows your schema documentation.

### Customizing Without Losing Changes
Generated files are overwritten by every `generate`, so don't edit them. Every generated class is `partial`, so put your additions in **your own files**, which the tool never touches:

```csharp
// AppDbContext.Custom.cs  (runs after the generated configuration, so it can override it)
public partial class AppDbContext
{
    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>().HasQueryFilter(u => !u.IsDeleted);
    }
}

// User.Custom.cs
public partial class User
{
    [NotMapped]
    public string FullName => $"{FirstName} {LastName}";
}
```

### PostgreSQL Types, Arrays and Enums
-   Postgres types are mapped to the matching .NET / Npgsql types, for example `timestamp` → `DateTime`, `time` → `TimeOnly`, `interval` → `TimeSpan`, `inet` → `IPAddress`, `int4range` → `NpgsqlRange<int>`, `tsvector` → `NpgsqlTsVector`.
-   Arrays become `T[]` (`integer[]` → `int[]`).
-   Where Npgsql's default would differ, the exact column type is pinned with `HasColumnType` (e.g. `timestamp without time zone`, `jsonb`).
-   Columns of unsupported types (e.g. PostGIS `geometry`) are skipped with a warning.
-   **Generated columns** (`GENERATED ALWAYS AS (...) STORED`, and `VIRTUAL` on PostgreSQL 18) get `HasComputedColumnSql(...)`, and **identity columns** get `UseIdentityAlwaysColumn()` / `UseIdentityByDefaultColumn()`, key or not. EF then never writes them and reads the database's values back after `SaveChanges`. Requires PostgreSQL 12 or later.

**Enums** are generated as C# enums. When your tables use enums, the generated `DbContext` gets a `ConfigureNpgsql` helper. Pass it to `UseNpgsql` (requires EF Core 9+):

```csharp
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString, AppDbContext.ConfigureNpgsql));
```

---

### Uninstalling
```powershell
dotnet tool uninstall -g DbToEntity.CLI
```
