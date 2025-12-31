using System;
using System.CommandLine;
using System.IO;
using System.Threading.Tasks;
using DbToEntity.Core;
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

            // 2.1 Propagate Class Names to Foreign Keys
            // Create lookup (Schema.Table -> ClassName)
            // Note: Postgres is case sensitive for values usually, but our names might be. 
            // We use simple matching assuming standard lowercase/exact match from metadata.
            var tableNameToClass = tables.ToDictionary(t => $"{t.Schema}.{t.Name}", t => t);

            foreach (var table in tables)
            {
                foreach (var fk in table.ForeignKeys)
                {
                    var key = $"{fk.TargetSchema}.{fk.TargetTable}";
                    if (tableNameToClass.TryGetValue(key, out var targetTable))
                    {
                        fk.TargetClassName = targetTable.ClassName;
                        fk.TargetClassNamePlural = targetTable.ClassName.Pluralize();
                    }
                    else
                    {
                        // Fallback if target not in generated set (should fit filtering logic, but safety first)
                        fk.TargetClassName = fk.TargetTable.Singularize().Pascalize();
                        fk.TargetClassNamePlural = fk.TargetClassName.Pluralize();
                    }
                }

                foreach (var fk in table.ReferencingForeignKeys)
                {
                    // Find the source table (the one that has this FK) to get its resolved ClassName
                    // Note: SourceTable name in FK metadata is raw table name.
                    // We must find the table in our generated list that matches schema/table.
                    // BUT: ForeignKeyMetadata does not have SourceSchema! It only has SourceTable.
                    // This is a flaw in previous implementation of MetadataProvider.
                    // However, we can assume unique table names globally OR try to strict match if we had schema.
                    // Since we don't have SourceSchema, we have to rely on SourceTable name.
                    // If multiple schemas have same table name, we might pick wrong one?
                    // YES. This is a risk.
                    // But typically FKs are loaded from constraint info which has schema. 
                    // Let's check MetadataProvider.LoadForeignKeysAsync...
                    // It selects `kcu.table_name`. 
                    // WE SHOULD HAVE ADDED SourceSchema to ForeignKeyMetadata.

                    // QUICK FIX: 
                    // Iterate all tables, find one where Name == fk.SourceTable AND it contains this FK instance (or equivalent).
                    // Actually, ReferencingForeignKeys contains the EXACT SAME object instance as in the source table's ForeignKeys list 
                    // IF we linked them by reference in MetadataProvider.
                    // Let's check PostgresMetadataProvider.GetTablesAsync.
                    // "targetTable.ReferencingForeignKeys.Add(fk);" -> It adds the same object.
                    // So we can find the table that contains this specific object in its ForeignKeys list.

                    var sourceTable = tables.FirstOrDefault(t => t.ForeignKeys.Contains(fk));
                    if (sourceTable != null)
                    {
                        fk.SourceClassName = sourceTable.ClassName;
                    }
                    else
                    {
                        // Fallback: If source table is not in the generation list (e.g. partial generation),
                        // we fall back to algorithmic naming.
                        fk.SourceClassName = fk.SourceTable.Singularize().Pascalize();
                    }
                }
            }

            // For Inverse Props, we need SourceClassName in FK Metadata?
            // Let's add SourceClassName to metadata too.

            // 3. Generate Code
            IEntityGenerator generator = new RoslynEntityGenerator();

            Directory.CreateDirectory(config.OutputDirectory);

            foreach (var table in tables)
            {
                Console.WriteLine($"Generating entity for {table.Name} (Class: {table.ClassName})...");

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
