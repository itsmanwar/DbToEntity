using DbToEntity.Core;
using DbToEntity.Core.Models;
using DbToEntity.Tests.Infrastructure;

namespace DbToEntity.Tests;

/// <summary>F7: db2entity.json, table exclusion and class-name overrides.</summary>
public class SettingsTests
{
    [Fact]
    public void Parse_AcceptsCommentsTrailingCommasAndAnyCase()
    {
        var settings = GeneratorSettings.Parse("""
            {
              // where the entities go
              "Output": "./Data/Entities",
              "context": "ShopContext",
              "pluralize": false,
              "excludeTables": ["audit_*", "legacy.*",],
              "classNames": { "tbl_usr": "User" },
            }
            """);

        Assert.Equal("./Data/Entities", settings.Output);
        Assert.Equal("ShopContext", settings.Context);
        Assert.False(settings.Pluralize);
        Assert.Equal(new[] { "audit_*", "legacy.*" }, settings.ExcludeTables);
        Assert.Equal("User", settings.ClassNames!["tbl_usr"]);
        Assert.Null(settings.Schema);
    }

    [Fact]
    public void Parse_RejectsUnknownSettings()
    {
        var ex = Assert.Throws<SettingsException>(() => GeneratorSettings.Parse("""{ "outptu": "./x" }"""));

        Assert.Contains("db2entity.json", ex.Message);
        Assert.Contains("outptu", ex.Message);
    }

    [Fact]
    public void Parse_RejectsMalformedJson()
    {
        Assert.Throws<SettingsException>(() => GeneratorSettings.Parse("""{ "output": """));
    }

    [Fact]
    public void Merge_CommandLineWins_UnsetValuesComeFromFile()
    {
        var file = new GeneratorSettings { Output = "./FromFile", Context = "FileContext", Pluralize = false, FluentOnly = true, ExcludeTables = new() { "x" } };
        var commandLine = new GeneratorSettings { Context = "CliContext", FluentOnly = false };

        var merged = GeneratorSettings.Merge(file, commandLine);

        Assert.Equal("./FromFile", merged.Output);
        Assert.Equal("CliContext", merged.Context);
        Assert.False(merged.Pluralize);   // from file
        Assert.False(merged.FluentOnly);  // explicit false on the command line beats true in the file
        Assert.Equal(new[] { "x" }, merged.ExcludeTables);
    }

    [Fact]
    public void ToJson_RoundTrips_AndOmitsUnsetValues()
    {
        var settings = new GeneratorSettings { Output = "./Entities", Pluralize = true, ClassNames = new() { ["a"] = "B" } };

        var json = settings.ToJson();
        var back = GeneratorSettings.Parse(json);

        Assert.DoesNotContain("connection", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("schema", json);
        Assert.Equal("./Entities", back.Output);
        Assert.True(back.Pluralize);
        Assert.Equal("B", back.ClassNames!["a"]);
    }

    private static SchemaBuilder Schema()
    {
        var s = new SchemaBuilder();
        var users = s.Table("users", "User", "*id");
        var audit = s.Table("audit_log", "AuditLog", "*id", "user_id");
        s.TableInSchema("legacy", "customers", "Customer", "*id");
        s.Table("__EFMigrationsHistory", "EFMigrationsHistory", "*MigrationId:text");
        s.ForeignKey("fk_audit_user", audit, "user_id", users);
        return s;
    }

    [Fact]
    public void ExcludeTables_MatchesGlobsOnNameOrSchemaQualifiedName_AndDropsTheirForeignKeys()
    {
        var s = Schema();

        var removed = ModelPipeline.ExcludeTables(s.Tables, new[] { "audit_*", "legacy.*" });

        Assert.Equal(new[] { "public.audit_log", "legacy.customers", "public.__EFMigrationsHistory" }, removed);
        Assert.Equal(new[] { "users" }, s.Tables.Select(t => t.Name));
        Assert.Empty(s.Tables.Single().ReferencingForeignKeys); // no navigation to a class that won't exist
        GeneratedModel.Build(s); // and the rest still compiles
    }

    [Fact]
    public void ExcludeTables_AlwaysDropsEfMigrationsHistory()
    {
        var s = Schema();

        ModelPipeline.ExcludeTables(s.Tables, null);

        Assert.DoesNotContain(s.Tables, t => t.Name == "__EFMigrationsHistory");
        Assert.Equal(3, s.Tables.Count);
    }

    [Fact]
    public void ExcludeTables_ExplicitlyRequestedTablesWin()
    {
        var s = Schema();

        ModelPipeline.ExcludeTables(s.Tables, new[] { "audit_*" }, keep: new[] { "audit_log", "__EFMigrationsHistory" });

        Assert.Contains(s.Tables, t => t.Name == "audit_log");
        Assert.Contains(s.Tables, t => t.Name == "__EFMigrationsHistory");
    }

    [Fact]
    public void ClassNameOverrides_ApplyByNameOrSchemaQualifiedName_AndFlowIntoRelationships()
    {
        var s = new SchemaBuilder();
        var usr = s.Table("tbl_usr", "", "*id");
        var ord = s.TableInSchema("sales", "orders", "", "*id", "tbl_usr_id");
        s.ForeignKey("fk_ord_usr", ord, "tbl_usr_id", usr);

        ModelPipeline.ResolveClassNames(s.Tables, overrides: new Dictionary<string, string> { ["tbl_usr"] = "User", ["sales.orders"] = "SalesOrder" });
        var model = GeneratedModel.Build(s);

        Assert.Equal(new[] { "User", "SalesOrder" }, s.Tables.Select(t => t.ClassName));
        var fk = Assert.Single(model.Entity("SalesOrder").GetForeignKeys());
        Assert.Equal("User", fk.PrincipalEntityType.ClrType.Name);
        Assert.Equal("SalesOrders", fk.PrincipalToDependent!.Name);
    }

    [Fact]
    public void ClassNameOverrides_RejectInvalidNames()
    {
        var tables = new List<TableMetadata> { new() { Schema = "public", Name = "users" } };

        var ex = Assert.Throws<SettingsException>(() => ModelPipeline.ResolveClassNames(tables, overrides: new Dictionary<string, string> { ["users"] = "My User" }));

        Assert.Contains("My User", ex.Message);
    }

    [Fact]
    public void ClassNameOverrides_RejectClashes()
    {
        var tables = new List<TableMetadata> { new() { Schema = "public", Name = "users" }, new() { Schema = "public", Name = "accounts" } };

        var ex = Assert.Throws<SettingsException>(() => ModelPipeline.ResolveClassNames(tables, overrides: new Dictionary<string, string> { ["accounts"] = "User" }));

        Assert.Contains("'User'", ex.Message);
        Assert.Contains("public.users", ex.Message);
    }
}
