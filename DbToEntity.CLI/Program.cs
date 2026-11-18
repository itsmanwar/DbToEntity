using System;
using System.CommandLine;
using System.IO;
using System.Threading.Tasks;
using DbToEntity.Core;
using DbToEntity.Core.Models;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using System.Linq;

using Humanizer;

namespace DbToEntity.CLI
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            var rootCommand = new RootCommand("PostgreSQL to EF Core Entity Generator");

            var generateCommand = new Command("generate", "Generate entities from database");

            var connectionOption = new Option<string>("--connection", "PostgreSQL connection string");

            var schemaOption = new Option<string>("--schema", "Database schema (optional)");

            var namespaceOption = new Option<string>("--namespace", "Namespace for generated code");
            namespaceOption.SetDefaultValue("GeneratedEntities");

            var outputOption = new Option<string>("--output", "Output directory");
            outputOption.SetDefaultValue("./Entities");

            var tableOption = new Option<string[]>("--tables", "Specific tables to generate (space separated)") { AllowMultipleArgumentsPerToken = true };

            var separateBySchemaOption = new Option<bool>("--separate-by-schema", "Organize entities into folders by schema");

            generateCommand.AddOption(connectionOption);
            generateCommand.AddOption(schemaOption);
            generateCommand.AddOption(namespaceOption);
            generateCommand.AddOption(outputOption);
            generateCommand.AddOption(tableOption);
            generateCommand.AddOption(separateBySchemaOption);

            generateCommand.SetHandler(async (connection, schema, namespaceName, output, tables, separate) =>
            {
                var finalConnection = GetConnectionString(connection);
                if (string.IsNullOrEmpty(finalConnection))
                {
                    Console.WriteLine("Error: Connection string not provided and not found in appsettings.json.");
                    return;
                }

                var finalNamespace = GetNamespace(namespaceName, output);

                await RunGenerate(new GeneratorConfig
                {
                    ConnectionString = finalConnection,
                    Schema = schema,
                    Namespace = finalNamespace,
                    OutputDirectory = output,
                    IncludedTables = tables != null ? new List<string>(tables) : new List<string>(),
                    SeparateBySchema = separate
                });
            }, connectionOption, schemaOption, namespaceOption, outputOption, tableOption, separateBySchemaOption);

            rootCommand.AddCommand(generateCommand);

            var updateCommand = new Command("update", "Update specific entities and DbContext");
            updateCommand.AddOption(connectionOption);
            updateCommand.AddOption(schemaOption);
            updateCommand.AddOption(namespaceOption);
            updateCommand.AddOption(outputOption);
            // reused options, tableOption is required for update ideally
            updateCommand.AddOption(tableOption);
            updateCommand.AddOption(separateBySchemaOption);

            updateCommand.SetHandler(async (connection, schema, namespaceName, output, tables, separate) =>
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

                var finalNamespace = GetNamespace(namespaceName, output);

                await RunUpdate(new GeneratorConfig
                {
                    ConnectionString = finalConnection,
                    Schema = schema,
                    Namespace = finalNamespace,
                    OutputDirectory = output,
                    IncludedTables = new List<string>(tables),
                    SeparateBySchema = separate
                });
            }, connectionOption, schemaOption, namespaceOption, outputOption, tableOption, separateBySchemaOption);

            rootCommand.AddCommand(updateCommand);

            return await rootCommand.InvokeAsync(args);
        }

        static async Task RunGenerate(GeneratorConfig config)
        {
            Console.WriteLine($"Starting generation for schema '{config.Schema ?? "all"}'...");

            // 1. Get Metadata
            IPostgresMetadataProvider metadataProvider = new PostgresMetadataProvider();
            var tables = await metadataProvider.GetTablesAsync(config.ConnectionString, config.Schema, config.IncludedTables.Count > 0 ? config.IncludedTables : null);

            Console.WriteLine($"Found {tables.Count} tables.");

            // 2. Resolve Class Names (Handle Duplicates)
            var duplicateNames = tables
                .GroupBy(t => t.Name.Singularize().Pascalize().Replace("_", ""))
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToHashSet();

            foreach (var table in tables)
            {
                var baseName = table.Name.Singularize().Pascalize().Replace("_", "");
                if (duplicateNames.Contains(baseName))
                {
                    // Collision detected: Use SalesOrder instead of Order
                    table.ClassName = table.Schema.Pascalize() + baseName;
                }
                else
                {
                    table.ClassName = baseName;
                }
            }

            // 2.1 Propagate class names to foreign keys and resolve navigation names
            ResolveForeignKeyNames(tables);

            // 3. Generate Code
            IEntityGenerator generator = new RoslynEntityGenerator();

            Directory.CreateDirectory(config.OutputDirectory);

            foreach (var table in tables)
            {
                Console.WriteLine($"Generating entity for {table.Name} (Class: {table.ClassName})...");

                var targetDirectory = config.OutputDirectory;
                var targetNamespace = config.Namespace;

                if (config.SeparateBySchema)
                {
                    // 1. Schema Folder (if not public)
                    if (!string.IsNullOrEmpty(table.Schema) && table.Schema != "public")
                    {
                        var schemaFolder = table.Schema.Pascalize();
                        targetDirectory = Path.Combine(targetDirectory, schemaFolder);
                        targetNamespace = $"{targetNamespace}.{schemaFolder}";
                    }

                    // 2. Type Folder (Tables/Views)
                    var typeFolder = table.Type.ToString().Pluralize(); // Tables, Views, MaterializedViews
                    targetDirectory = Path.Combine(targetDirectory, typeFolder);
                    targetNamespace = $"{targetNamespace}.{typeFolder}";

                    Directory.CreateDirectory(targetDirectory);
                }

                table.Namespace = targetNamespace; // Store for DbContext generation

                var file = generator.GenerateEntity(table, targetNamespace);
                var path = Path.Combine(targetDirectory, file.FileName);
                await File.WriteAllTextAsync(path, file.Content);
            }

            // For Generate, we overwrite DbContext
            Console.WriteLine("Generating DbContext...");
            var dbContextFile = generator.GenerateDbContext(tables, config.Namespace, config.DbContextName, config.SeparateBySchema); // Pass flag
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

            // 2. Resolve Class Names (Handle Duplicates for Updated Tables - this logic is partial but safe enough for updates if context is clean)
            // Ideally should check ALL tables to know global duplicates, but for MVP we assume local or re-fetch all names if critical.
            // For now, let's keep it simple: We re-calculate based on fetched set.
            var duplicateNames = tables
                .GroupBy(t => t.Name.Singularize().Pascalize())
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToHashSet();

            foreach (var table in tables)
            {
                var baseName = table.Name.Singularize().Pascalize();
                if (duplicateNames.Contains(baseName))
                {
                    table.ClassName = table.Schema.Pascalize() + baseName;
                }
                else
                {
                    table.ClassName = baseName;
                }
            }

            ResolveForeignKeyNames(tables);

            // 3. Regenerate Entities
            foreach (var table in tables)
            {
                Console.WriteLine($"Updating entity for {table.Name} (Class: {table.ClassName})...");

                var targetDirectory = config.OutputDirectory;
                var targetNamespace = config.Namespace;

                if (config.SeparateBySchema && !string.IsNullOrEmpty(table.Schema) && table.Schema != "public")
                {
                    var schemaFolder = table.Schema.Pascalize();
                    targetDirectory = Path.Combine(config.OutputDirectory, schemaFolder);
                    targetNamespace = $"{config.Namespace}.{schemaFolder}";
                    Directory.CreateDirectory(targetDirectory);
                }

                var file = generator.GenerateEntity(table, targetNamespace);
                var path = Path.Combine(targetDirectory, file.FileName);
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

        // Fills Target/Source class names on every FK from the resolved table class names, then
        // assigns navigation names. Must run after TableMetadata.ClassName is set.
        static void ResolveForeignKeyNames(List<TableMetadata> tables)
        {
            var tableByQualifiedName = tables.ToDictionary(t => $"{t.Schema}.{t.Name}", t => t);

            foreach (var table in tables)
            {
                foreach (var fk in table.ForeignKeys)
                {
                    if (tableByQualifiedName.TryGetValue($"{fk.TargetSchema}.{fk.TargetTable}", out var targetTable))
                    {
                        fk.TargetClassName = targetTable.ClassName;
                    }
                    else
                    {
                        // Target not in the loaded set: fall back to algorithmic naming
                        fk.TargetClassName = fk.TargetTable.Singularize().Pascalize();
                    }
                    fk.TargetClassNamePlural = fk.TargetClassName.Pluralize();
                }

                foreach (var fk in table.ReferencingForeignKeys)
                {
                    // The provider adds the same FK instance to the source's ForeignKeys and the target's
                    // ReferencingForeignKeys, so identity lookup finds the source table across schemas.
                    var sourceTable = tables.FirstOrDefault(t => t.ForeignKeys.Contains(fk));
                    fk.SourceClassName = sourceTable?.ClassName ?? fk.SourceTable.Singularize().Pascalize();
                }
            }

            EntityNaming.ResolveNavigationNames(tables);
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

        static string GetNamespace(string providedNamespace, string outputDirectory)
        {
            if (!string.IsNullOrEmpty(providedNamespace) && providedNamespace != "GeneratedEntities")
                return providedNamespace;

            // Try to detect csproj
            var currentDir = Directory.GetCurrentDirectory();
            var csproj = Directory.GetFiles(currentDir, "*.csproj").FirstOrDefault();
            if (csproj != null)
            {
                var baseNs = Path.GetFileNameWithoutExtension(csproj);

                // Calculate folder suffix if output directory is provided
                if (!string.IsNullOrEmpty(outputDirectory))
                {
                    try
                    {
                        var fullOutputDir = Path.GetFullPath(outputDirectory);
                        // Only add suffix if output dir is a subdirectory of current dir
                        if (fullOutputDir.StartsWith(currentDir, StringComparison.OrdinalIgnoreCase))
                        {
                            var relativePath = Path.GetRelativePath(currentDir, fullOutputDir);
                            if (relativePath != "." && !relativePath.StartsWith(".."))
                            {
                                var suffix = relativePath
                                    .Replace(Path.DirectorySeparatorChar, '.')
                                    .Replace(Path.AltDirectorySeparatorChar, '.')
                                    .Trim('.');
                                if (!string.IsNullOrEmpty(suffix))
                                {
                                    baseNs = $"{baseNs}.{suffix}";
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Ignore path errors, stick to base namespace
                    }
                }

                Console.WriteLine($"Detected namespace from project: {baseNs}");
                return baseNs;
            }

            return providedNamespace; // Return default if not found
        }
    }
}
