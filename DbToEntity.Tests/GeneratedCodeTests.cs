using DbToEntity.Core;
using DbToEntity.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DbToEntity.Tests;

/// <summary>
/// Generates code, compiles it and builds the EF model. Catches mismatches between
/// entity classes and the fluent DbContext configuration that unit tests can't see.
/// </summary>
public class GeneratedCodeTests
{
    private static string Describe(IForeignKey fk) =>
        $"{fk.DeclaringEntityType.ClrType.Name}.{fk.DependentToPrincipal?.Name} -> " +
        $"{fk.PrincipalEntityType.ClrType.Name}.{fk.PrincipalToDependent?.Name} " +
        $"[{string.Join(",", fk.Properties.Select(p => p.Name))}] {fk.GetConstraintName()}";

    private static string[] Relationships(GeneratedModel model) =>
        model.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()).Select(Describe).OrderBy(x => x).ToArray();

    [Fact]
    public void SingleFk_MapsOneRelationshipWithBothNavigations()
    {
        var s = new SchemaBuilder();
        var role = s.Table("roles", "Role", "*id", "name:varchar");
        var user = s.Table("users", "User", "*id", "role_id");
        s.ForeignKey("fk_user_role", user, "role_id", role);

        var model = GeneratedModel.Build(s.Tables);

        Assert.Equal(new[] { "User.Role -> Role.Users [RoleId] fk_user_role" }, Relationships(model));
    }

    [Fact]
    public void SelfReference_AndMultipleFksToSameTable_MapExactlyOneRelationshipPerFk()
    {
        var s = new SchemaBuilder();
        var file = s.Table("uploaded_files", "UploadedFile", "*id");
        var cat = s.Table("categories", "Category", "*id", "parent_id?", "categories"); // column collides with inverse name
        var p = s.Table("pensioners", "Pensioner", "*id", "photo_file_id", "sign_file_id?", "category_id");
        s.ForeignKey("fk_photo", p, "photo_file_id", file);
        s.ForeignKey("fk_sign", p, "sign_file_id", file);
        s.ForeignKey("fk_parent", cat, "parent_id", cat);
        s.ForeignKey("fk_cat", p, "category_id", cat);

        var model = GeneratedModel.Build(s.Tables);

        Assert.Equal(new[]
        {
            "Category.CategoryNav -> Category.CategoriesCollection [ParentId] fk_parent",
            "Pensioner.Category -> Category.Pensioners [CategoryId] fk_cat",
            "Pensioner.PhotoFile -> UploadedFile.PensionerPhotoFiles [PhotoFileId] fk_photo",
            "Pensioner.SignFile -> UploadedFile.PensionerSignFiles [SignFileId] fk_sign",
        }, Relationships(model));
    }

    [Fact]
    public void CompositeKeys_AndCompositeFk_Map()
    {
        var s = new SchemaBuilder();
        var item = s.Table("order_items", "OrderItem", "*order_id", "*line_no", "qty");
        var shipment = s.Table("shipments", "Shipment", "*id", "order_id", "line_no");
        s.ForeignKey("fk_ship_item", shipment, new[] { "order_id", "line_no" }, item, new[] { "order_id", "line_no" });

        var model = GeneratedModel.Build(s.Tables);

        Assert.Equal(new[] { "OrderId", "LineNo" }, model.Entity("OrderItem").FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.Equal(new[] { "Shipment.OrderItem -> OrderItem.Shipments [OrderId,LineNo] fk_ship_item" }, Relationships(model));
    }

    [Fact]
    public void PrimaryKeyColumnNamedLikeClass_UsesMemberSuffixInHasKey()
    {
        var s = new SchemaBuilder();
        s.Table("statuses", "Status", "*status:text", "label:text?");

        var model = GeneratedModel.Build(s.Tables);

        var key = model.Entity("Status").FindPrimaryKey()!;
        Assert.Equal("StatusMember", Assert.Single(key.Properties).Name);
        Assert.Equal("status", Assert.Single(key.Properties).GetColumnName());
    }

    [Fact]
    public void TableAndColumnMapping_PreservesDatabaseNames()
    {
        var s = new SchemaBuilder();
        s.TableInSchema("sales", "order_headers", "OrderHeader", "*id", "customer_name:varchar", "created_at:timestamptz?");

        var entity = GeneratedModel.Build(s.Tables).Entity("OrderHeader");

        Assert.Equal("order_headers", entity.GetTableName());
        Assert.Equal("sales", entity.GetSchema());
        Assert.Equal("customer_name", entity.FindProperty("CustomerName")!.GetColumnName());
        Assert.False(entity.FindProperty("CustomerName")!.IsNullable);
        Assert.True(entity.FindProperty("CreatedAt")!.IsNullable);
        Assert.Equal("order_headers_pkey", entity.FindPrimaryKey()!.GetName());
    }

    [Fact]
    public void View_WithoutPrimaryKey_IsKeyless()
    {
        var s = new SchemaBuilder();
        s.View("active_users", "ActiveUser", "id", "email:text");

        var entity = GeneratedModel.Build(s.Tables).Entity("ActiveUser");

        Assert.Null(entity.FindPrimaryKey());
        Assert.Equal("active_users", entity.GetViewName());
        Assert.Null(entity.GetTableName()); // regression: a [Table] attribute also mapped views to a table
    }

    [Fact]
    public void Generator_Throws_WhenNavigationNamesNotResolved()
    {
        var s = new SchemaBuilder();
        var role = s.Table("roles", "Role", "*id");
        var user = s.Table("users", "User", "*id", "role_id");
        s.ForeignKey("fk_user_role", user, "role_id", role);
        TypeMapper.ResolveColumnTypes(s.Tables, s.Enums);

        var ex = Assert.Throws<InvalidOperationException>(() => new RoslynEntityGenerator().GenerateEntity(user, "X"));
        Assert.Contains("ResolveNavigationNames", ex.Message);
    }

    [Fact]
    public void Generator_Throws_WhenColumnTypesNotResolved()
    {
        var s = new SchemaBuilder();
        var user = s.Table("users", "User", "*id");

        var ex = Assert.Throws<InvalidOperationException>(() => new RoslynEntityGenerator().GenerateEntity(user, "X"));
        Assert.Contains("ResolveColumnTypes", ex.Message);
    }
}
