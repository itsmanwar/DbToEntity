using DbToEntity.Core;
using DbToEntity.Core.Models;
using DbToEntity.Tests.Infrastructure;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.EntityFrameworkCore;

namespace DbToEntity.Tests;

/// <summary>
/// Schema names come from the database and may contain anything (quoted identifiers). They become folder
/// and namespace segments, so they must not escape the output folder or inject code, and ordinary names like
/// "my-schema" must still compile. (Security review finding: path traversal / generated-source injection.)
/// </summary>
public class SchemaNameSafetyTests
{
    [Theory]
    [InlineData("sales", "Sales")]
    [InlineData("cts_master", "CtsMaster")]
    [InlineData("my-schema", "MySchema")]
    [InlineData("2024_data", "S2024Data")]
    [InlineData("../../evil", "Evil")]
    [InlineData("..\\..\\evil", "Evil")]
    [InlineData("x;}class Evil{", "XClassEvil")]
    public void SchemaSegment_IsAPlainIdentifier(string schema, string expected)
    {
        var segment = EntityNaming.SchemaSegment(schema);

        Assert.Equal(expected, segment);
        Assert.True(SyntaxFacts.IsValidIdentifier(segment));
        Assert.Matches("^[A-Za-z][A-Za-z0-9]*$", segment);
    }

    [Theory]
    [InlineData("../../evil")]
    [InlineData("..\\..\\evil")]
    [InlineData("/etc")]
    [InlineData("C:\\Windows")]
    public void Locations_StayInsideTheOutputFolder(string schema)
    {
        var output = Path.Combine(Path.GetTempPath(), "db2entity-out");
        var table = new TableMetadata { Schema = schema, Name = "t" };
        var enumType = new EnumMetadata { Schema = schema, Name = "e" };

        var (tableDir, tableNs) = ModelPipeline.GetLocation(table, output, "App", separateBySchema: true);
        var (enumDir, enumNs) = ModelPipeline.GetEnumLocation(enumType, output, "App", separateBySchema: true);

        var root = Path.GetFullPath(output) + Path.DirectorySeparatorChar;
        Assert.StartsWith(root, Path.GetFullPath(tableDir) + Path.DirectorySeparatorChar);
        Assert.StartsWith(root, Path.GetFullPath(enumDir) + Path.DirectorySeparatorChar);
        Assert.All(tableNs.Split('.').Concat(enumNs.Split('.')), part => Assert.True(SyntaxFacts.IsValidIdentifier(part), part));
    }

    // Awkward and hostile schema names, with --separate-by-schema and a class-name clash (schema prefix),
    // must produce compilable code and nothing else.
    [Fact]
    public void AwkwardSchemaNames_GenerateValidCode()
    {
        var s = new SchemaBuilder();
        var a = s.TableInSchema("my-schema", "orders", "", "*id", "status:my-schema.status");
        var b = s.TableInSchema("2024_data", "orders", "", "*id", "a_id");
        s.TableInSchema("x;}class Evil{", "items", "", "*id");
        s.Enum("my-schema", "status", "open");
        s.ForeignKey("fk_b_a", b, "a_id", a);
        var reserved = ModelPipeline.ReservedNames(s.Tables, s.Enums, GeneratedModel.Namespace, separateBySchema: true);
        ModelPipeline.ResolveClassNames(s.Tables, reserved: reserved);

        var model = GeneratedModel.Build(s, separateBySchema: true);

        Assert.Equal(new[] { "MySchemaOrder", "S2024DataOrder", "Item" }, s.Tables.Select(t => t.ClassName));
        Assert.Contains("MySchema/Tables/MySchemaOrder.cs", model.Sources.Keys);
        Assert.Contains("MySchema/Enums/Status.cs", model.Sources.Keys);
        Assert.Contains("namespace Generated.XClassEvil.Tables", model.Sources["XClassEvil/Tables/Item.cs"]);
        Assert.DoesNotContain(model.Assembly.GetTypes(), t => t.Name == "Evil"); // nothing injected
        Assert.Equal("\"my-schema\".status", model.Entity("MySchemaOrder").FindProperty("Status")!.GetColumnType()); // Npgsql quotes it
    }

    [Fact]
    public void IdentifierFallback_IsDeterministic()
    {
        // Was a random GUID suffix for names with no usable characters, so names differed between runs.
        foreach (var name in new[] { "!!!", "", "-" })
        {
            Assert.Equal(EntityNaming.SanitizeIdentifier(name), EntityNaming.SanitizeIdentifier(name));
            Assert.True(SyntaxFacts.IsValidIdentifier(EntityNaming.SanitizeIdentifier(name)), name);
        }
    }
}
