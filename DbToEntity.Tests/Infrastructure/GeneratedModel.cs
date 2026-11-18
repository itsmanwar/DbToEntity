using System.Reflection;
using System.Runtime.Loader;
using DbToEntity.Core;
using DbToEntity.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DbToEntity.Tests.Infrastructure;

/// <summary>
/// End-to-end harness: resolves names, generates entities + DbContext, compiles them in memory
/// against EF Core, then builds the EF model with the Npgsql provider (no database needed).
/// Compile errors fail the test with the generated source attached.
/// </summary>
public sealed class GeneratedModel
{
    public const string Namespace = "Generated";
    public const string ContextName = "AppDbContext";

    private static readonly Lazy<MetadataReference[]> References = new(LoadReferences);
    private static int _assemblyCounter;

    public required Dictionary<string, string> Sources { get; init; }
    public required IModel Model { get; init; }

    public IEntityType Entity(string className) =>
        Model.GetEntityTypes().SingleOrDefault(e => e.ClrType.Name == className)
        ?? throw new Xunit.Sdk.XunitException($"Entity '{className}' not in model. Entities: {string.Join(", ", Model.GetEntityTypes().Select(e => e.ClrType.Name))}");

    public static GeneratedModel Build(List<TableMetadata> tables)
    {
        EntityNaming.ResolveNavigationNames(tables);

        var generator = new RoslynEntityGenerator();
        var sources = new Dictionary<string, string>();
        foreach (var table in tables)
        {
            table.Namespace = Namespace;
            var file = generator.GenerateEntity(table, Namespace);
            sources[file.FileName] = file.Content;
        }
        var context = generator.GenerateDbContext(tables, Namespace, ContextName);
        sources[context.FileName] = context.Content;

        var assembly = Compile(sources);
        return new GeneratedModel { Sources = sources, Model = CreateModel(assembly) };
    }

    private static Assembly Compile(Dictionary<string, string> sources)
    {
        var compilation = CSharpCompilation.Create(
            $"Generated_{Interlocked.Increment(ref _assemblyCounter)}",
            sources.Select(s => CSharpSyntaxTree.ParseText(s.Value, path: s.Key)),
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable));

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        if (!result.Success)
        {
            var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString());
            var dump = string.Join("\n\n", sources.Select(s => $"// ===== {s.Key}\n{s.Value}"));
            throw new Xunit.Sdk.XunitException($"Generated code does not compile:\n{string.Join("\n", errors)}\n\n{dump}");
        }

        stream.Position = 0;
        return AssemblyLoadContext.Default.LoadFromStream(stream);
    }

    private static IModel CreateModel(Assembly assembly)
    {
        var contextType = assembly.GetType($"{Namespace}.{ContextName}")
            ?? throw new Xunit.Sdk.XunitException($"{ContextName} not found in generated assembly");

        var builder = (DbContextOptionsBuilder)Activator.CreateInstance(typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType))!;
        builder.UseNpgsql("Host=unused"); // model building never opens a connection

        using var context = (DbContext)Activator.CreateInstance(contextType, builder.Options)!;
        return context.Model;
    }

    private static MetadataReference[] LoadReferences()
    {
        // Force-load the assemblies generated code depends on, then reference the framework plus test output folder.
        _ = typeof(DbContext).Assembly;
        _ = typeof(RelationalEntityTypeBuilderExtensions).Assembly;
        _ = typeof(NpgsqlDbContextOptionsBuilderExtensions).Assembly;

        var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var local = Directory.GetFiles(AppContext.BaseDirectory, "*.dll");

        return trusted.Concat(local)
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Where(IsManagedAssembly)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray();
    }

    private static bool IsManagedAssembly(string path)
    {
        try { AssemblyName.GetAssemblyName(path); return true; }
        catch (BadImageFormatException) { return false; }
    }
}
