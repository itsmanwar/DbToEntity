using DbToEntity.Core;
using DbToEntity.Core.Models;
using DbToEntity.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DbToEntity.Tests;

/// <summary>
/// A type named like a namespace of the generated code is shadowed by it (CS0118). Found on a real database:
/// schema message_queue + table message_queues -> class MessageQueue vs namespace ...MessageQueue.
/// </summary>
public class NamespaceCollisionTests
{
    private static List<TableMetadata> Tables(params (string Schema, string Name, string[] Columns)[] specs)
    {
        var s = new SchemaBuilder();
        foreach (var (schema, name, columns) in specs) s.TableInSchema(schema, name, "", columns);
        return s.Tables;
    }

    [Fact]
    public void SeparateBySchema_TableNamedLikeItsSchema_GetsEntitySuffix_AndCompiles()
    {
        var s = new SchemaBuilder();
        var queue = s.TableInSchema("message_queue", "message_queues", "", "*id", "payload:text");
        var users = s.Table("users", "", "*id");
        s.ForeignKey("fk_queue_user", queue, "id", users);
        var reserved = ModelPipeline.ReservedNames(s.Tables, s.Enums, GeneratedModel.Namespace, separateBySchema: true);

        var notes = ModelPipeline.ResolveClassNames(s.Tables, reserved: reserved);
        var model = GeneratedModel.Build(s, separateBySchema: true);

        Assert.Equal("MessageQueueEntity", queue.ClassName);
        Assert.Contains(notes, n => n.Contains("message_queue.message_queues") && n.Contains("MessageQueueEntity") && n.Contains("classNames"));
        Assert.Contains("MessageQueue/Tables/MessageQueueEntity.cs", model.Sources.Keys);
        Assert.Equal("message_queues", model.Entity("MessageQueueEntity").GetTableName());
        Assert.Contains("public DbSet<MessageQueueEntity> MessageQueueEntities", model.Sources["AppDbContext.cs"]);
    }

    [Fact]
    public void WithoutSeparateBySchema_SchemaNamesAreNotReserved()
    {
        var tables = Tables(("message_queue", "message_queues", new[] { "*id" }));
        var reserved = ModelPipeline.ReservedNames(tables, Array.Empty<EnumMetadata>(), "App", separateBySchema: false);

        Assert.Empty(ModelPipeline.ResolveClassNames(tables, reserved: reserved));
        Assert.Equal("MessageQueue", tables[0].ClassName);
    }

    [Fact]
    public void TableNamedLikeARootNamespacePart_IsRenamed_EvenWithoutSeparateBySchema()
    {
        var s = new SchemaBuilder();
        var t = s.Table("generated", "", "*id"); // root namespace in tests is "Generated"
        var reserved = ModelPipeline.ReservedNames(s.Tables, s.Enums, GeneratedModel.Namespace, separateBySchema: false);

        ModelPipeline.ResolveClassNames(s.Tables, reserved: reserved);
        var model = GeneratedModel.Build(s);

        Assert.Equal("GeneratedEntity", t.ClassName);
        Assert.NotNull(model.Entity("GeneratedEntity"));
    }

    [Fact]
    public void EnumNamedLikeASchemaFolder_GetsEnumSuffix_AndCompiles()
    {
        var s = new SchemaBuilder();
        s.TableInSchema("billing", "invoices", "Invoice", "*id");
        s.Enum("public", "billing", "monthly", "yearly");
        s.Table("plans", "Plan", "*id", "cycle:public.billing");

        var model = GeneratedModel.Build(s, separateBySchema: true);

        Assert.Contains("Enums/BillingEnum.cs", model.Sources.Keys);
        Assert.Equal("BillingEnum", model.Entity("Plan").FindProperty("Cycle")!.ClrType.Name);
    }

    [Fact]
    public void ClassNameOverride_ToAReservedName_IsAnError()
    {
        var tables = Tables(("message_queue", "message_queues", new[] { "*id" }));
        var reserved = ModelPipeline.ReservedNames(tables, Array.Empty<EnumMetadata>(), "App", separateBySchema: true);

        var ex = Assert.Throws<SettingsException>(() => ModelPipeline.ResolveClassNames(tables,
            overrides: new Dictionary<string, string> { ["message_queues"] = "MessageQueue" }, reserved: reserved));

        Assert.Contains("also a namespace", ex.Message);
    }

    [Fact]
    public void ReservedNames_CoverRootPartsSchemaFoldersAndTypeFolders()
    {
        var tables = Tables(("public", "a", new[] { "*id" }), ("cts_master", "b", new[] { "*id" }));
        var enums = new[] { new EnumMetadata { Schema = "ag", Name = "s" } };

        var reserved = ModelPipeline.ReservedNames(tables, enums, "ShopApp.Entities", separateBySchema: true);

        Assert.Equal(new[] { "Ag", "CtsMaster", "Entities", "Enums", "MaterializedViews", "ShopApp", "Tables", "Views" }, reserved.OrderBy(n => n));
    }
}
