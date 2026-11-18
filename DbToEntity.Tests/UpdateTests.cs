using DbToEntity.Core;
using DbToEntity.Core.Models;
using DbToEntity.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DbToEntity.Tests;

/// <summary>
/// The `update` command: start from a generated project (v1), change the database (v2), update some tables,
/// then compile the resulting files (untouched v1 files + regenerated ones + edited DbContext) and build the model.
/// </summary>
public class UpdateTests
{
    // v1 database: roles <- users <- orders, plus an unrelated products table.
    private static SchemaBuilder V1()
    {
        var s = new SchemaBuilder();
        var roles = s.Table("roles", "Role", "*id", "name:text");
        var users = s.Table("users", "User", "*id", "email:text", "role_id");
        var orders = s.Table("orders", "Order", "*id", "user_id", "product_id?", "total:numeric");
        s.Table("products", "Product", "*id", "title:text");
        s.ForeignKey("fk_users_role", users, "role_id", roles);
        s.ForeignKey("fk_orders_user", orders, "user_id", users);
        return s;
    }

    /// <summary>What RunUpdate does after loading metadata, minus the file system.</summary>
    private static (Dictionary<string, string> Files, List<string> Regenerated) Update(
        Dictionary<string, string> v1Files, SchemaBuilder v2, params string[] requested)
    {
        var (usedEnums, _) = ModelPipeline.Prepare(v2.Tables, v2.Enums);
        foreach (var t in v2.Tables) t.Namespace = GeneratedModel.Namespace;

        var regenerate = IncrementalUpdate.SelectTablesToRegenerate(v2.Tables, requested,
            t => v1Files.GetValueOrDefault($"{t.ClassName}.cs"));

        var generator = new RoslynEntityGenerator();
        var files = new Dictionary<string, string>(v1Files);
        foreach (var table in regenerate)
        {
            var file = generator.GenerateEntity(table, GeneratedModel.Namespace);
            files[file.FileName] = file.Content;
        }
        files["AppDbContext.cs"] = generator.UpdateDbContext(v1Files["AppDbContext.cs"], regenerate, v2.Tables, GeneratedModel.ContextName, usedEnums).Content;

        return (files, regenerate.Select(t => t.Name).ToList());
    }

    private static string[] Relationships(IModel model) =>
        model.GetEntityTypes().SelectMany(e => e.GetForeignKeys())
            .Select(fk => $"{fk.DeclaringEntityType.ClrType.Name}.{fk.DependentToPrincipal?.Name} -> {fk.PrincipalEntityType.ClrType.Name}.{fk.PrincipalToDependent?.Name}")
            .OrderBy(x => x).ToArray();

    private static string EntityBlock(string dbContext, string className)
    {
        var start = dbContext.IndexOf($"modelBuilder.Entity<{className}>", StringComparison.Ordinal);
        var end = dbContext.IndexOf("});", start, StringComparison.Ordinal);
        return dbContext[start..end];
    }

    [Fact]
    public void AddedColumn_IsMapped_AndRelationshipsToOtherTablesSurvive()
    {
        var v1 = GeneratedModel.Build(V1()).Sources;

        var v2 = V1();
        v2.Tables.Single(t => t.Name == "users").Columns.Add(new ColumnMetadata { Name = "phone", DataType = "text", IsNullable = true });

        var (files, regenerated) = Update(v1, v2, "users");
        var model = GeneratedModel.FromSources(files).Model;

        Assert.NotNull(model.GetEntityTypes().Single(e => e.ClrType.Name == "User").FindProperty("Phone"));
        Assert.Equal(new[] { "Order.User -> User.Orders", "User.Role -> Role.Users" }, Relationships(model));
        Assert.Equal(new[] { "roles", "users", "orders" }, regenerated); // users + both neighbours
    }

    [Fact]
    public void AddedForeignKey_AddsNavigationOnBothSides()
    {
        var v1 = GeneratedModel.Build(V1()).Sources;

        var v2 = V1();
        s_AddFk(v2, "fk_orders_product", "orders", "product_id", "products");

        var (files, regenerated) = Update(v1, v2, "orders");
        var model = GeneratedModel.FromSources(files).Model;

        Assert.Contains("Order.Product -> Product.Orders", Relationships(model));
        Assert.Contains("products", regenerated); // new principal gets its Orders collection
    }

    // Regression for the old-neighbour logic: without it users.cs keeps `ICollection<Order> Orders` with
    // [InverseProperty("User")], but Order.User no longer exists, so the project doesn't compile.
    [Fact]
    public void DroppedForeignKey_RemovesNavigationOnTheOtherSide()
    {
        var v1 = GeneratedModel.Build(V1()).Sources;

        var v2 = V1();
        var orders = v2.Tables.Single(t => t.Name == "orders");
        var fk = orders.ForeignKeys.Single();
        orders.ForeignKeys.Remove(fk);
        v2.Tables.Single(t => t.Name == "users").ReferencingForeignKeys.Remove(fk);

        var (files, regenerated) = Update(v1, v2, "orders");
        var model = GeneratedModel.FromSources(files).Model;

        Assert.Contains("users", regenerated);
        Assert.Equal(new[] { "User.Role -> Role.Users" }, Relationships(model));
    }

    [Fact]
    public void NewTable_GetsDbSetAndConfiguration_BeforeTheCustomizationHook()
    {
        var v1 = GeneratedModel.Build(V1()).Sources;

        var v2 = V1();
        var coupons = v2.Table("coupons", "Coupon", "*id", "code:text");
        var orders = v2.Tables.Single(t => t.Name == "orders");
        orders.Columns.Add(new ColumnMetadata { Name = "coupon_id", DataType = "int4", IsNullable = true });
        v2.ForeignKey("fk_orders_coupon", orders, "coupon_id", coupons);

        var (files, _) = Update(v1, v2, "coupons");
        var context = files["AppDbContext.cs"];
        var model = GeneratedModel.FromSources(files).Model;

        Assert.Contains("public DbSet<Coupon> Coupons { get; set; }", context);
        Assert.True(context.IndexOf("modelBuilder.Entity<Coupon>", StringComparison.Ordinal) < context.IndexOf("OnModelCreatingPartial(modelBuilder);", StringComparison.Ordinal));
        Assert.Contains("Order.Coupon -> Coupon.Orders", Relationships(model));
    }

    // Why update edits blocks instead of regenerating the whole DbContext: products changed in the database
    // too, but wasn't requested. Its old entity file has no `Sku`, so its configuration must stay as it was.
    [Fact]
    public void TablesNotRegenerated_KeepTheirConfigurationUnchanged()
    {
        var v1 = GeneratedModel.Build(V1()).Sources;

        var v2 = V1();
        v2.Tables.Single(t => t.Name == "products").Columns.Add(new ColumnMetadata { Name = "sku", DataType = "text" });
        v2.Tables.Single(t => t.Name == "users").Columns.Add(new ColumnMetadata { Name = "phone", DataType = "text", IsNullable = true });

        var (files, regenerated) = Update(v1, v2, "users");

        Assert.DoesNotContain("products", regenerated);
        Assert.Equal(v1["Product.cs"], files["Product.cs"]);
        Assert.Equal(EntityBlock(v1["AppDbContext.cs"], "Product"), EntityBlock(files["AppDbContext.cs"], "Product"));
        GeneratedModel.FromSources(files); // compiles and builds
    }

    [Fact]
    public void UpdatedDbContext_KeepsHeaderAndHook()
    {
        var v1 = GeneratedModel.Build(V1()).Sources;
        var v2 = V1();

        var (files, _) = Update(v1, v2, "users");

        Assert.StartsWith("// <auto-generated>", files["AppDbContext.cs"]);
        Assert.Contains("partial void OnModelCreatingPartial(ModelBuilder modelBuilder);", files["AppDbContext.cs"]);
    }

    [Fact]
    public void UpdatingWithNoChanges_LeavesDbContextIdentical()
    {
        var v1 = GeneratedModel.Build(V1()).Sources;

        var (files, _) = Update(v1, V1(), "users");

        Assert.Equal(v1["AppDbContext.cs"], files["AppDbContext.cs"]);
        Assert.Equal(v1["User.cs"], files["User.cs"]);
    }

    [Fact]
    public void ReadMappedTableNames_FindsTablesAndViews()
    {
        var s = V1();
        s.View("active_users", "ActiveUser", "id", "email:text");
        var context = GeneratedModel.Build(s).Sources["AppDbContext.cs"];

        Assert.Equal(new[] { "active_users", "orders", "products", "roles", "users" }, IncrementalUpdate.ReadMappedTableNames(context).OrderBy(n => n));
    }

    [Theory]
    [InlineData("public", ObjectType.Table, false, "Out", "App")]
    [InlineData("public", ObjectType.Table, true, "Out/Tables", "App.Tables")]
    [InlineData("sales", ObjectType.Table, true, "Out/Sales/Tables", "App.Sales.Tables")]
    [InlineData("sales", ObjectType.MaterializedView, true, "Out/Sales/MaterializedViews", "App.Sales.MaterializedViews")]
    public void GetLocation_IsTheSameForGenerateAndUpdate(string schema, ObjectType type, bool separate, string dir, string ns)
    {
        var table = new TableMetadata { Schema = schema, Name = "x", Type = type };

        var (directory, nameSpace) = ModelPipeline.GetLocation(table, "Out", "App", separate);

        Assert.Equal(dir.Replace('/', Path.DirectorySeparatorChar), directory);
        Assert.Equal(ns, nameSpace);
        Assert.Equal(ns, table.Namespace);
    }

    private static void s_AddFk(SchemaBuilder s, string name, string source, string column, string target) =>
        s.ForeignKey(name, s.Tables.Single(t => t.Name == source), column, s.Tables.Single(t => t.Name == target));
}
