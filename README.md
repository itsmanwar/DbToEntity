# DbToEntity

**A Smart EF Core Entity Generator for PostgreSQL**

`DbToEntity` is a CLI tool designed to generate Entity Framework Core entities from an existing PostgreSQL database. It distinguishes itself with:
-   **Partitioning Awareness**: Automatically detects partitioned tables and generates entities only for the parent table.
-   **Incremental Updates**: The `update` command allows you to regenerate specific entities non-destructively.
-   **Project Awareness**: Automatically detects your connection string from `appsettings.json` and namespace from your `.csproj` file.
-   **Fluent API**: Generates complete `OnModelCreating` configurations (Keys, Constraints, Relationships, Defaults) rather than just attributes.

---

## Installation

Install the tool globally from your local source (or NuGet if published):

```powershell
dotnet tool install --global --add-source "d:\Experiment\DOT NET\DB_to_Entity\DbToEntity.CLI\nupkg" DbToEntity.CLI
```

Verify installation:
```powershell
db2entity --version
```

---

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
-   `--schema`: (Optional) Schema to target (default: `public`).
-   `--output`: (Optional) Output directory (default: `./Entities`).
-   `--namespace`: (Optional) Namespace (default: detected from `.csproj`).
-   `--tables`: (Optional) Generate only specific tables (space separated).

### 2. Update Specific Tables (Incremental)

When you modify a specific table (e.g., adding a column to `users`), use `update` to refresh just that entity and the `DbContext` without checking out the whole database again.

```powershell
db2entity update --tables "users"
```

**Options:**
-   `--tables`: (Required) List of tables to update.
-   All other options from `generate` apply.

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
        entity.HasOne(d => d.Role).WithMany().HasForeignKey(d => d.RoleId);
    });
}
```

### Intelligent Naming
Uses `Humanizer` to ensure C# conventions:
-   Table `users` -> Class `User`
-   Column `first_name` -> Property `FirstName`
-   Foreign Key `role_id` -> Navigation `Role`

---

## Maintenance

### Updating the Tool
If you rebuild the package locally:
```powershell
dotnet pack "d:\Experiment\DOT NET\DB_to_Entity\DbToEntity.CLI\DbToEntity.CLI.csproj" -c Release
dotnet tool update --global --add-source "d:\Experiment\DOT NET\DB_to_Entity\DbToEntity.CLI\nupkg" DbToEntity.CLI
```

### Uninstalling
```powershell
dotnet tool uninstall -g DbToEntity.CLI
```
