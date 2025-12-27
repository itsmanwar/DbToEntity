using System;
using System.CommandLine;
using System.IO;
using System.Threading.Tasks;
using DbToEntity.Core;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using System.Linq;

namespace DbToEntity.CLI
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            var rootCommand = new RootCommand("PostgreSQL to EF Core Entity Generator");

            var generateCommand = new Command("generate", "Generate entities from database");

            var connectionOption = new Option<string>("--connection", "PostgreSQL connection string");

            var schemaOption = new Option<string>("--schema", "Database schema");
            schemaOption.SetDefaultValue("public");

            var namespaceOption = new Option<string>("--namespace", "Namespace for generated code");
            namespaceOption.SetDefaultValue("GeneratedEntities");

            var outputOption = new Option<string>("--output", "Output directory");
            outputOption.SetDefaultValue("./Entities");

            var tableOption = new Option<string[]>("--tables", "Specific tables to generate (space separated)") { AllowMultipleArgumentsPerToken = true };

            generateCommand.AddOption(connectionOption);
            generateCommand.AddOption(schemaOption);
            generateCommand.AddOption(namespaceOption);
            generateCommand.AddOption(outputOption);
            generateCommand.AddOption(tableOption);

            generateCommand.SetHandler(async (connection, schema, namespaceName, output, tables) =>
            {
                var finalConnection = GetConnectionString(connection);
                if (string.IsNullOrEmpty(finalConnection))
                {
                    Console.WriteLine("Error: Connection string not provided and not found in appsettings.json.");
                    return;
                }

                var finalNamespace = GetNamespace(namespaceName);

                await RunGenerate(new GeneratorConfig
                {
                    ConnectionString = finalConnection,
                    Schema = schema,
                    Namespace = finalNamespace,
                    OutputDirectory = output,
                    IncludedTables = tables != null ? new List<string>(tables) : new List<string>()
                });
            }, connectionOption, schemaOption, namespaceOption, outputOption, tableOption);

            rootCommand.AddCommand(generateCommand);

            var updateCommand = new Command("update", "Update specific entities and DbContext");
            updateCommand.AddOption(connectionOption);
            updateCommand.AddOption(schemaOption);
            updateCommand.AddOption(namespaceOption);
            updateCommand.AddOption(outputOption);
            // reused options, tableOption is required for update ideally
            updateCommand.AddOption(tableOption);

            updateCommand.SetHandler(async (connection, schema, namespaceName, output, tables) =>
            {
                if (tables == null || tables.Length == 0)
                {
                    Console.WriteLine("Error: Please specify tables to update using --tables");
                    return;
                }

                var finalConnection = GetConnectionString(connection);
                if (string.IsNullOrEmpty(finalConnection))
                {
                    Console.WriteLine("Error: Connection string not provided and not found in appsettings.json.");
                    return;
                }

                var finalNamespace = GetNamespace(namespaceName);

                await RunUpdate(new GeneratorConfig
                {
                    ConnectionString = finalConnection,
                    Schema = schema,
                    Namespace = finalNamespace,
                    OutputDirectory = output,
                    IncludedTables = new List<string>(tables)
                });
            }, connectionOption, schemaOption, namespaceOption, outputOption, tableOption);

            rootCommand.AddCommand(updateCommand);

            return await rootCommand.InvokeAsync(args);
        }

        static async Task RunGenerate(GeneratorConfig config)
        {
            Console.WriteLine($"Starting generation for schema '{config.Schema}'...");

            // 1. Get Metadata
            IPostgresMetadataProvider metadataProvider = new PostgresMetadataProvider();
            var tables = await metadataProvider.GetTablesAsync(config.ConnectionString, config.Schema, config.IncludedTables.Count > 0 ? config.IncludedTables : null);

            Console.WriteLine($"Found {tables.Count} tables.");

            // 2. Generate Code
            IEntityGenerator generator = new RoslynEntityGenerator();

            Directory.CreateDirectory(config.OutputDirectory);

            foreach (var table in tables)
            {
                Console.WriteLine($"Generating entity for {table.Name}...");
                var file = generator.GenerateEntity(table, config.Namespace);
                var path = Path.Combine(config.OutputDirectory, file.FileName);
                await File.WriteAllTextAsync(path, file.Content);
            }

            // For Generate, we overwrite DbContext
            Console.WriteLine("Generating DbContext...");
            var dbContextFile = generator.GenerateDbContext(tables, config.Namespace, config.DbContextName);
            await File.WriteAllTextAsync(Path.Combine(config.OutputDirectory, dbContextFile.FileName), dbContextFile.Content);

            Console.WriteLine("Generation complete.");
        }

        static async Task RunUpdate(GeneratorConfig config)
        {
            Console.WriteLine($"Starting update for {config.IncludedTables.Count} tables...");

            // 1. Get Metadata for specific tables
            IPostgresMetadataProvider metadataProvider = new PostgresMetadataProvider();
            var tables = await metadataProvider.GetTablesAsync(config.ConnectionString, config.Schema, config.IncludedTables);

            if (tables.Count == 0)
            {
                Console.WriteLine("No tables found to update.");
                return;
            }

            IEntityGenerator generator = new RoslynEntityGenerator();
            Directory.CreateDirectory(config.OutputDirectory);

            // 2. Regenerate Entities
            foreach (var table in tables)
            {
                Console.WriteLine($"Updating entity for {table.Name}...");
                var file = generator.GenerateEntity(table, config.Namespace);
                var path = Path.Combine(config.OutputDirectory, file.FileName);
                await File.WriteAllTextAsync(path, file.Content);
            }

            // 3. Update DbContext
            var dbContextPath = Path.Combine(config.OutputDirectory, $"{config.DbContextName}.cs");
            if (File.Exists(dbContextPath))
            {
                Console.WriteLine("Updating DbContext...");
                var existingCode = await File.ReadAllTextAsync(dbContextPath);
                var updatedFile = generator.UpdateDbContext(existingCode, tables, config.DbContextName);
                if (updatedFile.Content != existingCode)
                {
                    await File.WriteAllTextAsync(dbContextPath, updatedFile.Content);
                    Console.WriteLine("DbContext updated.");
                }
                else
                {
                    Console.WriteLine("DbContext already up to date.");
                }
            }
            else
            {
                Console.WriteLine("Warning: DbContext not found. Skipping DbContext update.");
            }

            Console.WriteLine("Update complete.");
        }
        static string GetConnectionString(string providedConnection)
        {
            if (!string.IsNullOrEmpty(providedConnection)) return providedConnection;

            // Try to read from appsettings.json
            var currentDir = Directory.GetCurrentDirectory();
            var builder = new ConfigurationBuilder()
                .SetBasePath(currentDir)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false);

            var configuration = builder.Build();

            // Try standard keys
            var conn = configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrEmpty(conn))
            {
                conn = configuration.GetConnectionString("Postgres");
            }
            if (string.IsNullOrEmpty(conn))
            {
                // Fallback: check "ConnectionStrings:*"
                var section = configuration.GetSection("ConnectionStrings");
                if (section.Exists())
                {
                    conn = section.GetChildren().FirstOrDefault()?.Value;
                }
            }

            if (!string.IsNullOrEmpty(conn))
            {
                Console.WriteLine("Using connection string from appsettings.json");
            }

            return conn;
        }

        static string GetNamespace(string providedNamespace)
        {
            if (!string.IsNullOrEmpty(providedNamespace) && providedNamespace != "GeneratedEntities")
                return providedNamespace;

            // Try to detect csproj
            var csproj = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.csproj").FirstOrDefault();
            if (csproj != null)
            {
                var ns = Path.GetFileNameWithoutExtension(csproj);
                Console.WriteLine($"Detected namespace from project: {ns}");
                return ns;
            }

            return providedNamespace; // Return default if not found
        }
    }
}
