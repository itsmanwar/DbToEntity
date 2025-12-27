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

You can install `dbgen` as a global .NET tool.

### 1. Pack the Tool
Navigate to the root of the solution and pack the CLI project:

```powershell
dotnet pack "d:\Experiment\DOT NET\DB_to_Entity\DbEntityGenerator.CLI\DbEntityGenerator.CLI.csproj" -c Release
```
This generates a `.nupkg` file in `DbEntityGenerator.CLI\nupkg`.

### 2. Install Globally
Install the tool from your local source:

```powershell
dotnet tool install --global --add-source "d:\Experiment\DOT NET\DB_to_Entity\DbEntityGenerator.CLI\nupkg" DbEntityGenerator.CLI
```

You can now verify installation by running `dbgen --version`.

---

## Usage

### 1. Generate Entities (Initial Scaffold)
Use the `generate` command to scaffold the entire database or a specific schema.

```powershell
dbgen generate --connection "Host=localhost;Database=mydb;Username=postgres;Password=password" --schema "public" --output "./Data/Entities" --namespace "MyApp.Domain"
```

**Options:**
-   `--connection`: (Required) PostgreSQL connection string.
-   `--schema`: (Optional) Database schema to target (default: `public`).
-   `--output`: (Optional) Directory for generated files (default: `./Generated`).
-   `--namespace`: (Optional) Namespace for the classes (default: `Generated`).

### 2. Update Specific Tables (Incremental)
Use the `update` command when you have modified specific tables and want to update their entities without regenerating the whole project.

```powershell
dbgen update --connection "Host=localhost;Database=mydb;..." --tables "users,orders" --output "./Data/Entities"
```

**Options:**
-   `--tables`: (Required) Comma-separated list of table names to update.
-   All other options from `generate` apply.

---

## Updating the Tool

If you pull the latest code and want to update your installed version:

1.  **Check Version**: Open `DbEntityGenerator.CLI\DbEntityGenerator.CLI.csproj` and ensure the `<Version>` tag is incremented (e.g., `1.0.5`).
2.  **Repack**:
    ```powershell
    dotnet pack "d:\Experiment\DOT NET\DB_to_Entity\DbEntityGenerator.CLI\DbEntityGenerator.CLI.csproj" -c Release
    ```
3.  **Update**:
    ```powershell
    dotnet tool update --global --add-source "d:\Experiment\DOT NET\DB_to_Entity\DbEntityGenerator.CLI\nupkg" DbEntityGenerator.CLI
    ```

---

## Uninstall

To remove the tool from your system:

```powershell
dotnet tool uninstall -g DbEntityGenerator.CLI
```
