using DbToEntity.Core.Models;
using DbToEntity.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DbToEntity.Tests;

public class OneToOneTests
{
    private static void UniqueIndex(TableMetadata table, params string[] columns) =>
        table.Indexes.Add(new IndexMetadata { Name = $"ux_{table.Name}_{string.Join("_", columns)}", IsUnique = true, Columns = columns.ToList() });

    private static IForeignKey SingleFk(GeneratedModel model, string dependent) =>
        Assert.Single(model.Entity(dependent).GetForeignKeys());

    [Fact]
    public void FkWithUniqueIndex_IsOneToOne_WithSingularReferenceInverse()
    {
        var s = new SchemaBuilder();
        var user = s.Table("users", "User", "*id");
        var profile = s.Table("user_profiles", "UserProfile", "*id", "user_id", "bio:text?");
        UniqueIndex(profile, "user_id");
        s.ForeignKey("fk_profile_user", profile, "user_id", user, onDelete: ReferentialAction.Cascade);

        var model = GeneratedModel.Build(s);
        var fk = SingleFk(model, "UserProfile");

        Assert.True(fk.IsUnique);
        Assert.Equal("User", fk.DependentToPrincipal!.Name);
        Assert.Equal("UserProfile", fk.PrincipalToDependent!.Name);
        Assert.False(fk.PrincipalToDependent.IsCollection);
        Assert.Equal("UserProfile", fk.PrincipalToDependent.ClrType.Name);
        Assert.Equal("UserProfile", fk.DeclaringEntityType.ClrType.Name); // the FK side is the dependent
        Assert.Equal(DeleteBehavior.Cascade, fk.DeleteBehavior);
        Assert.Contains(".WithOne(p => p.UserProfile).HasForeignKey<UserProfile>(d => d.UserId)", model.Sources["AppDbContext.cs"]);
    }

    [Fact]
    public void SharedPrimaryKey_IsOneToOne()
    {
        var s = new SchemaBuilder();
        var user = s.Table("users", "User", "*id");
        var settings = s.Table("user_settings", "UserSetting", "*user_id", "theme:text");
        s.ForeignKey("fk_settings_user", settings, "user_id", user);

        var fk = SingleFk(GeneratedModel.Build(s), "UserSetting");

        Assert.True(fk.IsUnique);
        Assert.Equal("UserSetting", fk.PrincipalToDependent!.Name);
    }

    [Fact]
    public void CompositeFk_MatchingUniqueIndexInAnyOrder_IsOneToOne()
    {
        var s = new SchemaBuilder();
        var item = s.Table("order_items", "OrderItem", "*order_id", "*line_no");
        var note = s.Table("item_notes", "ItemNote", "*id", "order_id", "line_no");
        UniqueIndex(note, "line_no", "order_id");
        s.ForeignKey("fk_note_item", note, new[] { "order_id", "line_no" }, item, new[] { "order_id", "line_no" });

        Assert.True(SingleFk(GeneratedModel.Build(s), "ItemNote").IsUnique);
    }

    [Theory]
    [InlineData("wider")]      // unique over (user_id, kind): many rows per user
    [InlineData("partial")]    // unique only WHERE ...: not guaranteed
    [InlineData("expression")] // e.g. (user_id, lower(x)): Columns only lists user_id
    [InlineData("non-unique")]
    public void FkWithoutAGuaranteeOfUniqueness_StaysOneToMany(string kind)
    {
        var s = new SchemaBuilder();
        var user = s.Table("users", "User", "*id");
        var address = s.Table("addresses", "Address", "*id", "user_id", "kind:text");
        address.Indexes.Add(kind switch
        {
            "wider" => new IndexMetadata { Name = "ux", IsUnique = true, Columns = { "user_id", "kind" } },
            "partial" => new IndexMetadata { Name = "ux", IsUnique = true, IsPartial = true, Columns = { "user_id" } },
            "expression" => new IndexMetadata { Name = "ux", IsUnique = true, HasExpressions = true, Columns = { "user_id" } },
            _ => new IndexMetadata { Name = "ix", IsUnique = false, Columns = { "user_id" } },
        });
        s.ForeignKey("fk_address_user", address, "user_id", user);

        var fk = SingleFk(GeneratedModel.Build(s), "Address");

        Assert.False(fk.IsUnique);
        Assert.True(fk.PrincipalToDependent!.IsCollection);
        Assert.Equal("Addresses", fk.PrincipalToDependent.Name);
    }

    [Fact]
    public void SeveralOneToOnesFromSameTable_AreNamedAfterTheirColumns()
    {
        var s = new SchemaBuilder();
        var address = s.Table("addresses", "Address", "*id");
        var customer = s.Table("customers", "Customer", "*id", "billing_address_id?", "shipping_address_id?");
        UniqueIndex(customer, "billing_address_id");
        UniqueIndex(customer, "shipping_address_id");
        s.ForeignKey("fk_billing", customer, "billing_address_id", address);
        s.ForeignKey("fk_shipping", customer, "shipping_address_id", address);

        var model = GeneratedModel.Build(s);
        var names = model.Entity("Address").GetNavigations().Select(n => (n.Name, n.IsCollection)).OrderBy(n => n.Name);

        Assert.Equal(new[] { ("CustomerBillingAddress", false), ("CustomerShippingAddress", false) }, names);
    }

    [Fact]
    public void SelfReferencingOneToOne_GetsDistinctNames()
    {
        var s = new SchemaBuilder();
        var employee = s.Table("employees", "Employee", "*id", "successor_id?");
        UniqueIndex(employee, "successor_id");
        s.ForeignKey("fk_successor", employee, "successor_id", employee);

        var fk = SingleFk(GeneratedModel.Build(s), "Employee");

        Assert.True(fk.IsUnique);
        Assert.Equal(("EmployeeNav", "EmployeeInverse"), (fk.DependentToPrincipal!.Name, fk.PrincipalToDependent!.Name));
    }
}
