using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using Microsoft.CodeAnalysis.CSharp;
using Npgsql;
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

            var connectionOption = new Option<string>("--connection", "PostgreSQL connection string (default: appsettings.json)");
            var schemaOption = new Option<string>("--schema", "Database schema (default: all schemas)");
            var namespaceOption = new Option<string>("--namespace", "Namespace for generated code (default: from the .csproj)");
            namespaceOption.SetDefaultValue("GeneratedEntities");
            var outputOption = new Option<string>("--output", "Output directory");
            outputOption.SetDefaultValue("./Entities");
            var tableOption = new Option<string[]>("--tables", "Specific tables (space separated)") { AllowMultipleArgumentsPerToken = true };
            var separateBySchemaOption = new Option<bool>("--separate-by-schema", "Organize entities into folders by schema");
            var contextOption = new Option<string>("--context", "DbContext class name");
            contextOption.SetDefaultValue("AppDbContext");
            var noPluralizeOption = new Option<bool>("--no-pluralize", "Keep table names as they are: no singular class names, no plural DbSet/collection names");
            var fluentOnlyOption = new Option<bool>("--fluent-only", "Plain entity classes without attributes; all mapping in the DbContext");

            var options = new Option[] { connectionOption, schemaOption, namespaceOption, outputOption, tableOption, separateBySchemaOption, contextOption, noPluralizeOption, fluentOnlyOption };

            var generateCommand = new Command("generate", "Generate entities and the DbContext from the database");
            var updateCommand = new Command("update", "Regenerate specific tables (and related ones) and refresh the DbContext");
            foreach (var option in options)
            {
                generateCommand.AddOption(option);
                updateCommand.AddOption(option);
            }

            // Builds the config from the parsed options; null (with a message) when the input is invalid.
            GeneratorConfig? BuildConfig(ParseResult parse, bool requireTables)
            {
                var tables = parse.GetValueForOption(tableOption) ?? Array.Empty<string>();
                if (requireTables && tables.Length == 0)
                {
                    Console.Error.WriteLine("Error: Please specify tables to update using --tables");
                    return null;
                }

                var connection = GetConnectionString(parse.GetValueForOption(connectionOption));
                if (string.IsNullOrEmpty(connection))
                {
                    Console.Error.WriteLine("Error: Connection string not provided and not found in appsettings.json.");
                    return null;
                }

                var output = parse.GetValueForOption(outputOption)!;
                var contextName = parse.GetValueForOption(contextOption)!;
                if (!SyntaxFacts.IsValidIdentifier(contextName))
                {
                    Console.Error.WriteLine($"Error: --context '{contextName}' is not a valid C# class name.");
                    return null;
                }

                return new GeneratorConfig
                {
                    ConnectionString = connection,
                    Schema = parse.GetValueForOption(schemaOption),
                    Namespace = GetNamespace(parse.GetValueForOption(namespaceOption)!, output),
                    OutputDirectory = output,
                    IncludedTables = tables.ToList(),
                    SeparateBySchema = parse.GetValueForOption(separateBySchemaOption),
                    DbContextName = contextName,
                    Pluralize = !parse.GetValueForOption(noPluralizeOption),
                    FluentOnly = parse.GetValueForOption(fluentOnlyOption),
                };
            }

            generateCommand.SetHandler(async (InvocationContext context) =>
            {
                var config = BuildConfig(context.ParseResult, requireTables: false);
                context.ExitCode = config == null ? 1 : await RunSafely(() => RunGenerate(config));
            });

            updateCommand.SetHandler(async (InvocationContext context) =>
            {
                var config = BuildConfig(context.ParseResult, requireTables: true);
                context.ExitCode = config == null ? 1 : await RunSafely(() => RunUpdate(config));
            });

            rootCommand.AddCommand(generateCommand);
            rootCommand.AddCommand(updateCommand);

            return await rootCommand.InvokeAsync(args);
        }

        // Exit code 0 on success, 1 on any failure, with a one-line message instead of a stack trace.
        static async Task<int> RunSafely(Func<Task<int>> run)
        {
            try
            {
                return await run();
            }
            catch (NpgsqlException ex)
            {
                Console.Error.WriteLine($"Error: database error: {ex.Message}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Error: could not write files: {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Console.Error.WriteLine("Set DB2ENTITY_DEBUG=1 for details.");
                if (Environment.GetEnvironmentVariable("DB2ENTITY_DEBUG") == "1") Console.Error.WriteLine(ex);
            }
            return 1;
        }

        static async Task<int> RunGenerate(GeneratorConfig config)
        {
            Console.WriteLine($"Starting generation for schema '{config.Schema ?? "all"}'...");

            IPostgresMetadataProvider metadataProvider = new PostgresMetadataProvider();
            var tables = await metadataProvider.GetTablesAsync(config.ConnectionString, config.Schema, config.IncludedTables.Count > 0 ? config.IncludedTables : null);
            var enums = await metadataProvider.GetEnumsAsync(config.ConnectionString);
            Console.WriteLine($"Found {tables.Count} tables.");

            if (tables.Count == 0)
            {
                Console.Error.WriteLine("Error: no tables found. Check --schema / --tables and the connection's database.");
                return 1;
            }

            ModelPipeline.ResolveClassNames(tables, config.Pluralize);
            var usedEnums = Prepare(tables, enums, config);

            IEntityGenerator generator = new RoslynEntityGenerator(config.FluentOnly);
            Directory.CreateDirectory(config.OutputDirectory);

            foreach (var table in tables)
            {
                Console.WriteLine($"Generating entity for {table.Name} (Class: {table.ClassName})...");
                await WriteEntityAsync(generator, table, config);
            }

            await WriteEnumsAsync(generator, usedEnums, config);

            Console.WriteLine("Generating DbContext...");
            var dbContextFile = generator.GenerateDbContext(tables, config.Namespace, config.DbContextName, config.SeparateBySchema, usedEnums);
            await File.WriteAllTextAsync(Path.Combine(config.OutputDirectory, dbContextFile.FileName), dbContextFile.Content);

            PrintEnumHint(usedEnums, config);
            Console.WriteLine("Generation complete.");
            return 0;
        }

        static async Task<int> RunUpdate(GeneratorConfig config)
        {
            Console.WriteLine($"Starting update for {config.IncludedTables.Count} table(s)...");

            // The project's table set = tables the existing DbContext maps + the requested ones. Loading all of
            // them keeps relationships between them; tables never generated stay out, so no navigation points at
            // a class the project doesn't have.
            var dbContextPath = Path.Combine(config.OutputDirectory, $"{config.DbContextName}.cs");
            string? existingDbContext = File.Exists(dbContextPath) ? await File.ReadAllTextAsync(dbContextPath) : null;
            if (existingDbContext == null)
            {
                Console.WriteLine($"Warning: {dbContextPath} not found. Only the requested tables are loaded, so relationships to other tables are skipped. Run 'generate' to create the DbContext.");
            }

            var tableSet = config.IncludedTables
                .Concat(existingDbContext != null ? IncrementalUpdate.ReadMappedTableNames(existingDbContext) : Enumerable.Empty<string>())
                .Distinct()
                .ToList();

            IPostgresMetadataProvider metadataProvider = new PostgresMetadataProvider();
            var tables = await metadataProvider.GetTablesAsync(config.ConnectionString, config.Schema, tableSet);
            var enums = await metadataProvider.GetEnumsAsync(config.ConnectionString);

            var missing = config.IncludedTables.Where(n => tables.All(t => t.Name != n)).ToList();
            if (missing.Count > 0)
            {
                Console.WriteLine($"Warning: not found in the database: {string.Join(", ", missing)}");
            }
            if (missing.Count == config.IncludedTables.Count)
            {
                Console.Error.WriteLine("Error: none of the requested tables were found.");
                return 1;
            }

            // Same naming over the same table set as 'generate', so class and navigation names stay stable.
            ModelPipeline.ResolveClassNames(tables, config.Pluralize);
            var usedEnums = Prepare(tables, enums, config);
            foreach (var table in tables)
            {
                ModelPipeline.GetLocation(table, config.OutputDirectory, config.Namespace, config.SeparateBySchema);
            }

            var toRegenerate = IncrementalUpdate.SelectTablesToRegenerate(tables, config.IncludedTables, table =>
            {
                var (directory, _) = ModelPipeline.GetLocation(table, config.OutputDirectory, config.Namespace, config.SeparateBySchema);
                var path = Path.Combine(directory, $"{table.ClassName}.cs");
                return File.Exists(path) ? File.ReadAllText(path) : null;
            });

            var related = toRegenerate.Where(t => !config.IncludedTables.Contains(t.Name)).Select(t => t.Name).ToList();
            if (related.Count > 0)
            {
                Console.WriteLine($"Also regenerating related tables (their navigations depend on the updated tables): {string.Join(", ", related)}");
            }

            IEntityGenerator generator = new RoslynEntityGenerator(config.FluentOnly);
            Directory.CreateDirectory(config.OutputDirectory);

            foreach (var table in toRegenerate)
            {
                Console.WriteLine($"Updating entity for {table.Name} (Class: {table.ClassName})...");
                await WriteEntityAsync(generator, table, config);
            }

            await WriteEnumsAsync(generator, usedEnums, config);

            if (existingDbContext != null)
            {
                Console.WriteLine("Updating DbContext...");
                var updated = generator.UpdateDbContext(existingDbContext, toRegenerate, tables, config.DbContextName, usedEnums);
                if (updated.Content != existingDbContext)
                {
                    await File.WriteAllTextAsync(dbContextPath, updated.Content);
                    Console.WriteLine("DbContext updated.");
                }
                else
                {
                    Console.WriteLine("DbContext already up to date.");
                }
            }

            PrintEnumHint(usedEnums, config);
            Console.WriteLine("Update complete.");
            return 0;
        }

        static List<EnumMetadata> Prepare(List<TableMetadata> tables, List<EnumMetadata> enums, GeneratorConfig config)
        {
            var (usedEnums, warnings) = ModelPipeline.Prepare(tables, enums, config.Pluralize);
            foreach (var warning in warnings)
            {
                Console.WriteLine($"Warning: {warning}");
            }
            return usedEnums;
        }

        static async Task WriteEntityAsync(IEntityGenerator generator, TableMetadata table, GeneratorConfig config)
        {
            var (directory, ns) = ModelPipeline.GetLocation(table, config.OutputDirectory, config.Namespace, config.SeparateBySchema);
            Directory.CreateDirectory(directory);
            var file = generator.GenerateEntity(table, ns);
            await File.WriteAllTextAsync(Path.Combine(directory, file.FileName), file.Content);
        }

        static async Task WriteEnumsAsync(IEntityGenerator generator, List<EnumMetadata> enums, GeneratorConfig config)
        {
            foreach (var enumType in enums)
            {
                Console.WriteLine($"Generating enum for {enumType.Schema}.{enumType.Name} (Enum: {enumType.ClassName})...");
                var file = generator.GenerateEnum(enumType, config.Namespace);
                await File.WriteAllTextAsync(Path.Combine(config.OutputDirectory, file.FileName), file.Content);
            }
        }

        static void PrintEnumHint(List<EnumMetadata> usedEnums, GeneratorConfig config)
        {
            if (usedEnums.Count == 0) return;
            Console.WriteLine($"Note: {usedEnums.Count} PostgreSQL enum(s) mapped. Register them where the context is configured:");
            Console.WriteLine($"      options.UseNpgsql(connectionString, {config.DbContextName}.ConfigureNpgsql)   (requires EF Core 9+)");
        }

        static string? GetConnectionString(string? providedConnection)
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
