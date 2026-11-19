# DbToEntity

[![NuGet](https://img.shields.io/nuget/v/MH.DbToEntity.CLI.svg)](https://www.nuget.org/packages/MH.DbToEntity.CLI)
[![Downloads](https://img.shields.io/nuget/dt/MH.DbToEntity.CLI.svg)](https://www.nuget.org/packages/MH.DbToEntity.CLI)
[![CI](https://github.com/itsmanwar/DbToEntity/actions/workflows/ci.yml/badge.svg)](https://github.com/itsmanwar/DbToEntity/actions/workflows/ci.yml)

**Generate EF Core entities and a `DbContext` from an existing PostgreSQL database, and keep them in sync as the database changes.**

```powershell
dotnet tool install --global MH.DbToEntity.CLI
db2entity generate          # inside your project: reads appsettings.json and the .csproj
```

## Why DbToEntity?

-   **Zero configuration in a project.** It finds the connection string in `appsettings.json` and the namespace from your `.csproj`.
-   **Incremental `update`.** Regenerate just the tables that changed. Your other entities and `DbContext` configuration stay as they are.
-   **Understands PostgreSQL.** It generates the parent of partitioned tables only, maps enums, arrays, `jsonb`, ranges, `inet`, generated and identity columns, and reads views and materialized views as keyless entities.
-   **Matches the database exactly.** Keys, constraint names, `ON DELETE` rules, one-to-one relationships, alternate keys, indexes, defaults and comments.
-   **Safe to regenerate.** Everything is `partial`, so your own code lives in separate files that the tool never touches.
-   **Modern C#.** Nullable reference types follow the database's `NOT NULL`, so there are no warnings whatever your project's `<Nullable>` setting.

## Contents

-   [Requirements](#requirements)
-   [Installation](#installation)
-   [Quick Start](#quick-start)
-   [Commands](#commands)
-   [Settings File](#settings-file-db2entityjson)
-   [What Gets Generated](#what-gets-generated)
-   [Customizing Without Losing Changes](#customizing-without-losing-changes)
-   [Upgrading to 2.0](#upgrading-to-20)
-   [Troubleshooting](#troubleshooting)
-   [Contributing](#contributing)

---

## Requirements

| | |
|---|---|
| To run the tool | .NET 8 runtime or SDK |
| Database | PostgreSQL 12 or later |
| In your project | [`Npgsql.EntityFrameworkCore.PostgreSQL`](https://www.nuget.org/packages/Npgsql.EntityFrameworkCore.PostgreSQL). Version 9 or later if your tables use PostgreSQL enums. |

## Installation

**Global** (available everywhere as `db2entity`):

```powershell
dotnet tool install --global MH.DbToEntity.CLI
dotnet tool update  --global MH.DbToEntity.CLI     # later, to upgrade
```

**Local** (version pinned per repository, so the whole team uses the same one):

```powershell
dotnet new tool-manifest        # once per repository
dotnet tool install MH.DbToEntity.CLI
dotnet db2entity generate       # note the "dotnet" prefix
```

Check which one you're running with `db2entity --version`. A global and a local install can have different versions.

## Quick Start

1.  In your project folder, with a connection string in `appsettings.json` (or `appsettings.Development.json`) named `DefaultConnection` or `Postgres`:

    ```powershell
    dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL
    db2entity generate
    ```

    This creates `./Entities` with one class per table plus `AppDbContext`, using your project's namespace (`MyApp.Entities`). It also saves the settings it used to `db2entity.json`.

2.  Register the context in `Program.cs`:

    ```csharp
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
    ```

    If your tables use PostgreSQL enums, pass the generated helper as well: `options.UseNpgsql(connectionString, AppDbContext.ConfigureNpgsql)`. See [Enums](#enums).

3.  When the database changes later:

    ```powershell
    db2entity update --tables users orders
    ```

---

## Commands

### `generate`: scaffold everything

```powershell
db2entity generate --connection "Host=localhost;Database=shop;Username=app;Password=..." --schema public --output ./Data/Entities --namespace MyApp.Data
```

| Option | Default | Description |
|---|---|---|
| `--connection` | from `appsettings.json` | PostgreSQL connection string. Never saved to `db2entity.json`. |
| `--schema` | all schemas | Only this schema. |
| `--tables` | all tables | Only these tables (space separated). |
| `--output` | `./Entities` | Output folder. |
| `--namespace` | from the `.csproj` + folder | Namespace of the generated code. |
| `--context` | `AppDbContext` | `DbContext` class name. |
| `--separate-by-schema` | off | Folders and namespaces per schema and kind (see [Folder layout](#folder-layout)). |
| `--no-pluralize` | off | Keep table names as they are: `users` → class `Users` (default: class `User`, DbSet `Users`). |
| `--fluent-only` | off | Plain classes without attributes. All mapping, including indexes, goes in the `DbContext`. |
| `--config` | `./db2entity.json` | Settings file to use. |

`__EFMigrationsHistory` is always skipped. Both commands exit with code `1` and a one-line message on any error, so they're safe in scripts and CI.

### `update`: refresh some tables

```powershell
db2entity update --tables users orders
```

-   Regenerates the listed tables, plus the tables whose navigation properties depend on them (e.g. after adding or dropping a foreign key). The tool lists them.
-   In the `DbContext`, only those tables' configuration blocks are replaced, and missing `DbSet`s are added. Everything else stays as it was.
-   To add a new table, name it in `--tables`.
-   It reads the settings `generate` saved in `db2entity.json`, so names and folders match without repeating options.

## Settings File (`db2entity.json`)

`generate` writes the settings it used to `db2entity.json` in the current folder, and both commands read it. Commit it, so everyone generates the same way. You can also edit it by hand:

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

-   **Precedence:** command-line options, then `db2entity.json`, then the defaults.
-   Comments and trailing commas are allowed. Unknown settings are errors, so typos don't go unnoticed.
-   **A `--connection` value is never saved**, since it usually contains a password. Use `connectionStringName`, or add `"connection"` yourself.
-   Tables named in `--tables` are generated even when an `excludeTables` pattern matches them.

---

## What Gets Generated

For these tables:

```sql
CREATE TABLE roles (id serial PRIMARY KEY, name varchar(50) NOT NULL);
CREATE TABLE users (
    id       int GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    email    varchar(255) NOT NULL,
    nickname text,
    role_id  int NOT NULL REFERENCES roles (id) ON DELETE RESTRICT
);
COMMENT ON TABLE users IS 'Registered users';
```

`User.cs`:

```csharp
/// <summary>
/// Registered users
/// </summary>
[Table("users")]
public partial class User
{
    [Column("id"), Key]
    public int Id { get; set; }

    [Column("email"), Required, StringLength(255)]
    public string Email { get; set; } = null!;

    [Column("nickname")]
    public string? Nickname { get; set; }

    [Column("role_id")]
    public int RoleId { get; set; }

    [ForeignKey("RoleId"), InverseProperty("Users")]
    public virtual Role Role { get; set; } = null!;
}
```

`AppDbContext.cs` (excerpt):

```csharp
public partial class AppDbContext : DbContext
{
    public DbSet<Role> Roles { get; set; } = null!;
    public DbSet<User> Users { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>((entity) =>
        {
            entity.ToTable("users", tb => tb.HasComment("Registered users"));
            entity.HasKey(e => e.Id).HasName("users_pkey");
            entity.Property(e => e.Id).HasColumnName("id").UseIdentityAlwaysColumn().IsRequired();
            entity.Property(e => e.Email).HasColumnName("email").IsRequired().HasMaxLength(255);
            entity.HasOne(d => d.Role).WithMany(p => p.Users).HasForeignKey(d => d.RoleId)
                .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_users_role");
        });
        // ...
        OnModelCreatingPartial(modelBuilder);   // your configuration, applied last
    }
}
```

Use `--fluent-only` to get plain classes and keep all mapping in the `DbContext`.

### Naming

-   Tables become singular PascalCase classes (`order_items` → `OrderItem`), and DbSets and collections are plural (`OrderItems`).
-   Columns become PascalCase properties (`first_name` → `FirstName`).
-   A reference navigation is named after the target (`role_id` → `Role`). With several foreign keys to the same table, it's named after the column (`billing_address_id` → `BillingAddress`).
-   Same table name in two schemas: the class gets a schema prefix (`SalesOrder`).
-   Rename any class with `classNames` in `db2entity.json`.

### Relationships

-   **`ON DELETE`** becomes an explicit `OnDelete(...)`: CASCADE → `Cascade`, SET NULL → `SetNull`, RESTRICT → `Restrict`, NO ACTION → `ClientSetNull`.
-   **One-to-one** is detected when the foreign key columns are the primary key or have a unique constraint. Both sides then get a single reference instead of a collection.
-   **Foreign keys to a unique column** other than the primary key get `HasPrincipalKey(...)`.
-   A navigation is optional (`Role?`) when any of its foreign key columns is nullable.

### PostgreSQL types

-   Types map to the matching .NET and Npgsql types: `timestamp` → `DateTime`, `time` → `TimeOnly`, `interval` → `TimeSpan`, `inet` → `IPAddress`, `int4range` → `NpgsqlRange<int>`, `tsvector` → `NpgsqlTsVector`, `hstore` → `Dictionary<string, string>`, and more.
-   Arrays become `T[]` (`integer[]` → `int[]`).
-   Where Npgsql's default store type would differ, the exact column type is pinned with `HasColumnType` (e.g. `timestamp without time zone`, `jsonb`).
-   **Generated columns** (`GENERATED ALWAYS AS (...) STORED`, and `VIRTUAL` on PostgreSQL 18) get `HasComputedColumnSql(...)`. **Identity columns** get `UseIdentityAlwaysColumn()` or `UseIdentityByDefaultColumn()`. EF then never writes these columns, and reads the database's values back after `SaveChanges`.
-   Columns of unsupported types (e.g. PostGIS `geometry`) are skipped with a warning.

### Enums

PostgreSQL enums become C# enums, with `[PgName]` keeping the database labels:

```csharp
public enum Mood
{
    [PgName("happy")] Happy,
    [PgName("very sad")] VerySad
}
```

When your tables use enums, the `DbContext` gets a `ConfigureNpgsql` helper that registers them. Pass it to `UseNpgsql`. This needs EF Core 9 or later:

```csharp
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString, AppDbContext.ConfigureNpgsql));
```

### Tables, views and partitions

-   **Partitioned tables:** only the parent is generated, not one class per partition. Classic `INHERITS` children are separate tables and are kept.
-   **Views and materialized views** become keyless entities (`HasNoKey()`, `ToView(...)`).
-   **Comments** (`COMMENT ON TABLE/COLUMN`) become `/// <summary>` doc comments and `HasComment(...)`, so IntelliSense shows your schema documentation.

### Folder layout

By default, all files go in the output folder. With `--separate-by-schema`, files are grouped by schema and kind, and the namespaces match the folders:

```
Entities/
  AppDbContext.cs                      MyApp.Data
  Tables/User.cs                       MyApp.Data.Tables            (public schema: no schema folder)
  Views/ActiveUser.cs                  MyApp.Data.Views
  Enums/Mood.cs                        MyApp.Data.Enums
  Sales/Tables/Order.cs                MyApp.Data.Sales.Tables
  Sales/Enums/OrderStatus.cs           MyApp.Data.Sales.Enums
  Logs/MaterializedViews/DailyStat.cs  MyApp.Data.Logs.MaterializedViews
```

-   Generated files import the namespaces they need, including entities and enums from other schemas.
-   A class that would have the same name as one of these namespaces (e.g. `MessageQueue` in schema `message_queue`) gets an `Entity`/`Enum` suffix, and the tool tells you. Choose another name with `classNames`.

---

## Customizing Without Losing Changes

Generated files are overwritten on every run, so don't edit them. Every generated class is `partial`, so put your additions in **your own files**, which the tool never touches:

```csharp
// AppDbContext.Custom.cs: runs after the generated configuration, so it can override it
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
    public string DisplayName => Nickname ?? Email;
}
```

## Upgrading to 2.0

2.0 fixes code that didn't compile or mapped the database incorrectly, so regenerating changes most files. Review the diff, and check that:

-   **Your edits live in your own files.** Move custom code into partial classes, and `DbContext` configuration into `OnModelCreatingPartial` (see above).
-   **Enums are registered.** Pass `AppDbContext.ConfigureNpgsql` to `UseNpgsql`. This needs **EF Core 9 or later**.
-   **Delete behaviour matches the database.** Relationships now get an explicit `OnDelete(...)` from each foreign key. Before, EF assumed cascade delete for every required relationship.
-   **Nullability is annotated.** Generated files use `#nullable enable` (`string?`, `= null!`), so code that reads entities may get new nullable warnings in projects with `<Nullable>enable</Nullable>`.
-   **Views aren't mapped as tables.** Views no longer get `[Table]`, which made migrations try to create a table for them.
-   **`db2entity.json` is new.** Commit it. It never contains a `--connection` value.

## Troubleshooting

| Problem | Fix |
|---|---|
| `Package type 'DotnetTool' is not supported` | It's a command-line tool, not a library. Don't add it with *Manage NuGet Packages*; install it with `dotnet tool install` (see [Installation](#installation)). |
| Enum columns come out as `object`, or 2.0 features are missing | You're running an older version. Check `db2entity --version`, then `dotnet tool update --global MH.DbToEntity.CLI`. With a local tool, `dotnet db2entity` and `db2entity` can be different versions. |
| `Connection string not provided and not found in appsettings.json` | Run from the folder containing `appsettings.json`, name the connection string `DefaultConnection` or `Postgres` (or set `connectionStringName`), or pass `--connection`. |
| Npgsql errors about an enum type when reading or saving | The enums aren't registered: pass `AppDbContext.ConfigureNpgsql` to `UseNpgsql` (EF Core 9+). |
| Warning that a column was skipped | Its type isn't supported yet (e.g. PostGIS). Map it yourself in a partial class and `OnModelCreatingPartial`. |

## Uninstalling

```powershell
dotnet tool uninstall --global MH.DbToEntity.CLI
```

## Contributing

```powershell
dotnet build
dotnet test     # unit tests; the PostgreSQL integration tests also run when Docker is available
```

The integration tests start a throwaway `postgres:17` container. See [`docs/FINDINGS.md`](docs/FINDINGS.md) for the roadmap and known gaps, and [`docs/RELEASING.md`](docs/RELEASING.md) for how releases are published.

## License

MIT
