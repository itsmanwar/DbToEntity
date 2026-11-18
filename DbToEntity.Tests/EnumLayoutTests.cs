using DbToEntity.Core;
using DbToEntity.Core.Models;
using DbToEntity.Tests.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.EntityFrameworkCore;

namespace DbToEntity.Tests;

/// <summary>--separate-by-schema puts enums in &lt;Schema&gt;/Enums with a matching namespace.</summary>
public class EnumLayoutTests
{
    // public.users uses a public enum and a sales enum; sales.orders uses the sales enum.
    private static SchemaBuilder Shop()
    {
        var s = new SchemaBuilder();
        s.Enum("public", "mood", "happy", "sad");
        s.Enum("sales", "order_status", "pending", "shipped");
        var users = s.Table("users", "User", "*id", "mood:public.mood", "last_order_status:sales.order_status?");
        var orders = s.TableInSchema("sales", "orders", "Order", "*id", "user_id", "status:sales.order_status", "history:sales._order_status?");
        s.ForeignKey("fk_orders_user", orders, "user_id", users);
        return s;
    }

    [Theory]
    [InlineData("public", false, "Out", "App")]
    [InlineData("sales", false, "Out", "App")]
    [InlineData("public", true, "Out/Enums", "App.Enums")]
    [InlineData("sales", true, "Out/Sales/Enums", "App.Sales.Enums")]
    public void GetEnumLocation(string schema, bool separate, string dir, string ns)
    {
        var e = new EnumMetadata { Schema = schema, Name = "x" };

        var (directory, nameSpace) = ModelPipeline.GetEnumLocation(e, "Out", "App", separate);

        Assert.Equal(dir.Replace('/', Path.DirectorySeparatorChar), directory);
        Assert.Equal(ns, nameSpace);
        Assert.Equal(ns, e.Namespace);
    }

    [Fact]
    public void SeparateBySchema_EnumsGoToSchemaEnumsFolders_AndEverythingCompiles()
    {
        var model = GeneratedModel.Build(Shop(), separateBySchema: true);

        Assert.Contains("Enums/Mood.cs", model.Sources.Keys);
        Assert.Contains("Sales/Enums/OrderStatus.cs", model.Sources.Keys);
        Assert.Contains("namespace Generated.Sales.Enums", model.Sources["Sales/Enums/OrderStatus.cs"]);

        // Entities import the enum namespaces they use, including another schema's.
        var user = model.Sources["Tables/User.cs"];
        Assert.Contains("using Generated.Enums;", user);
        Assert.Contains("using Generated.Sales.Enums;", user);
        Assert.Contains("using Generated.Sales.Enums;", model.Sources["Sales/Tables/Order.cs"]);
        Assert.DoesNotContain("using Generated.Enums;", model.Sources["Sales/Tables/Order.cs"]); // only what it uses

        // The DbContext's ConfigureNpgsql needs both.
        var context = model.Sources["AppDbContext.cs"];
        Assert.Contains("using Generated.Enums;", context);
        Assert.Contains("using Generated.Sales.Enums;", context);

        // And EF maps them to the Postgres enums.
        Assert.Equal("sales.order_status", model.Entity("Order").FindProperty("Status")!.GetColumnType());
        Assert.Equal("sales.order_status[]", model.Entity("Order").FindProperty("History")!.GetColumnType());
        Assert.Equal("public.mood", model.Entity("User").FindProperty("Mood")!.GetColumnType());
        Assert.Empty(model.Diagnostics(NullableContextOptions.Enable).Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString()));
    }

    // Regression (existed before the enum layout): with --separate-by-schema, entities related across schemas
    // didn't import each other's namespace, so a cross-schema FK broke the build.
    [Fact]
    public void SeparateBySchema_RelatedEntitiesInOtherSchemasAreImported()
    {
        var s = new SchemaBuilder();
        var users = s.Table("users", "User", "*id");
        var orders = s.TableInSchema("sales", "orders", "Order", "*id", "user_id");
        s.ForeignKey("fk_orders_user", orders, "user_id", users);

        var model = GeneratedModel.Build(s, separateBySchema: true);

        Assert.Contains("using Generated.Tables;", model.Sources["Sales/Tables/Order.cs"]);       // Order.User
        Assert.Contains("using Generated.Sales.Tables;", model.Sources["Tables/User.cs"]);       // User.Orders
        Assert.Equal("User", Assert.Single(model.Entity("Order").GetForeignKeys()).PrincipalEntityType.ClrType.Name);
    }

    [Fact]
    public void WithoutSeparateBySchema_EnumsStayNextToEntities()
    {
        var model = GeneratedModel.Build(Shop());

        Assert.Contains("Mood.cs", model.Sources.Keys);
        Assert.Contains("OrderStatus.cs", model.Sources.Keys);
        Assert.Contains("namespace Generated\r\n", model.Sources["OrderStatus.cs"]);
        Assert.DoesNotContain(".Enums;", model.Sources["User.cs"]);
    }

    // `update` on a DbContext that had no enums yet must add the enum namespace for ConfigureNpgsql.
    [Fact]
    public void Update_AddsEnumUsingsToTheDbContext()
    {
        var v1Schema = new SchemaBuilder();
        v1Schema.Table("users", "User", "*id", "name:text");
        var v1 = GeneratedModel.Build(v1Schema, separateBySchema: true).Sources;

        var v2 = new SchemaBuilder();
        var mood = v2.Enum("sales", "order_status", "pending");
        var users = v2.Table("users", "User", "*id", "name:text", "status:sales.order_status?");
        var (enumsUsed, _) = ModelPipeline.Prepare(v2.Tables, v2.Enums);
        ModelPipeline.GetLocation(users, "", GeneratedModel.Namespace, true);
        var (_, enumNs) = ModelPipeline.GetEnumLocation(mood, "", GeneratedModel.Namespace, true);

        var generator = new RoslynEntityGenerator();
        var files = new Dictionary<string, string>(v1)
        {
            ["Tables/User.cs"] = generator.GenerateEntity(users, users.Namespace).Content,
            ["Sales/Enums/OrderStatus.cs"] = generator.GenerateEnum(mood, enumNs).Content,
            ["AppDbContext.cs"] = generator.UpdateDbContext(v1["AppDbContext.cs"], v2.Tables, v2.Tables, GeneratedModel.ContextName, enumsUsed).Content,
        };

        Assert.Contains("using Generated.Sales.Enums;", files["AppDbContext.cs"]);
        var model = GeneratedModel.FromSources(files);
        Assert.Equal("sales.order_status", model.Entity("User").FindProperty("Status")!.GetColumnType());
    }
}
