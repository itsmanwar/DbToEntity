# DbToEntity

**A Custom EF Core Entity Generator for PostgreSQL**

`DbToEntity` is a CLI tool designed to generate Entity Framework Core entities from an existing PostgreSQL database. It is specifically built to handle **Partitioned Tables** (generating entities only for parent tables) and supports **Incremental Updates** (updating specific entities without overwriting the entire model).

---

## Features

-   **PostgreSQL Support**: tailored for PostgreSQL databases using `Npgsql`.
-   **Partitioning Awareness**: Automatically detects partitioned tables and generates entities only for the parent table, keeping your domain model clean.
-   **Incremental Updates**: The `update` command allows you to regenerate specific entities and update the `DbContext` non-destructively.
-   **Intelligent Naming**: Uses `Humanizer` to generate singular class names (e.g., `users` -> `User`) and plural `DbSet` properties (e.g., `Users`).
-   **Data Annotations**: Generates standard attributes like `[Table]`, `[Key]`, `[Required]`, `[StringLength]`.
-   **Fluent API Support**: Generates full `OnModelCreating` configuration including:
    -   `ToTable("name", "schema")`
    -   `HasKey`, `HasName` (Constraint names)
    -   `HasDefaultValueSql`
    -   Relationships (`HasOne`, `WithMany`)

---

## Prerequisites

-   .NET 8.0 SDK or later.
-   A running PostgreSQL instance.

---

## Installation

You can install `db2entity` as a global .NET tool.

### 1. Pack the Tool
Navigate to the root of the solution and pack the CLI project:

```powershell
dotnet pack "d:\Experiment\DOT NET\DB_to_Entity\DbToEntity.CLI\DbToEntity.CLI.csproj" -c Release
```
This generates a `.nupkg` file in `DbToEntity.CLI\nupkg`.

### 2. Install Globally
Install the tool from your local source:

```powershell
dotnet tool install --global --add-source "d:\Experiment\DOT NET\DB_to_Entity\DbToEntity.CLI\nupkg" DbToEntity.CLI
```

You can now verify installation by running `db2entity --version`.

---

## Quick Start

If you are running this inside a .NET project that already has an **`appsettings.json`** with a connection string:

1.  **Install**:
    ```powershell
    dotnet tool install --global --add-source "d:\Experiment\DOT NET\DB_to_Entity\DbToEntity.CLI\nupkg" DbToEntity.CLI
    ```
2.  **Run**:
    ```powershell
    db2entity generate
    ```

That's it! The tool will:
-   Found connection string in `appsettings.json`.
-   Use your project name as the namespace.
-   Generate entities in `./Entities`.

---

## Detailed Usage

### 1. Generate Entities
Full syntax if you need to override defaults:

```powershell
db2entity generate --connection "..." --schema "public" --output "./Data/Entities"
```

**Options:**
-   `--connection`: (Optional) PostgreSQL connection string. If omitted, attempts to read `ConnectionStrings:DefaultConnection` from `appsettings.json`.
-   `--schema`: (Optional) Database schema to target (default: `public`).
-   `--output`: (Optional) Directory for generated files (default: `./Entities`).
-   `--namespace`: (Optional) Namespace for the classes. If omitted, detects from the current project's `.csproj` file.

### 2. Update Specific Tables (Incremental)
Use the `update` command when you have modified specific tables and want to update their entities without regenerating the whole project.

```powershell
db2entity update --connection "Host=localhost;Database=mydb;..." --tables "users,orders" --output "./Data/Entities"
```

**Options:**
-   `--tables`: (Required) Comma-separated list of table names to update.
-   All other options from `generate` apply.

---

## Updating the Tool

If you pull the latest code and want to update your installed version:

1.  **Check Version**: Open `DbToEntity.CLI\DbToEntity.CLI.csproj` and ensure the `<Version>` tag is incremented (e.g., `1.0.7`).
2.  **Repack**:
    ```powershell
    dotnet pack "d:\Experiment\DOT NET\DB_to_Entity\DbToEntity.CLI\DbToEntity.CLI.csproj" -c Release
    ```
3.  **Update**:
    ```powershell
    dotnet tool update --global --add-source "d:\Experiment\DOT NET\DB_to_Entity\DbToEntity.CLI\nupkg" DbToEntity.CLI
    ```

---

## Uninstall

To remove the tool from your system:

```powershell
dotnet tool uninstall -g DbToEntity.CLI
```
