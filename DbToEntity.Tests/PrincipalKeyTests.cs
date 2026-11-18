using DbToEntity.Core.Models;
using DbToEntity.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DbToEntity.Tests;

/// <summary>FKs that reference a unique column other than the principal's primary key (an alternate key).</summary>
public class PrincipalKeyTests
{
    private static IForeignKey SingleFk(GeneratedModel model, string dependent) =>
        Assert.Single(model.Entity(dependent).GetForeignKeys());

    [Fact]
    public void FkToUniqueNonPkColumn_UsesThatColumnAsPrincipalKey()
    {
        var s = new SchemaBuilder();
        var country = s.Table("countries", "Country", "*id", "iso_code:bpchar(2)");
        country.Indexes.Add(new IndexMetadata { Name = "ux_iso", IsUnique = true, Columns = { "iso_code" } });
        var city = s.Table("cities", "City", "*id", "country_code:bpchar(2)");
        s.ForeignKey("fk_city_country", city, "country_code", country, "iso_code");

        var fk = SingleFk(GeneratedModel.Build(s), "City");

        Assert.Equal(new[] { "IsoCode" }, fk.PrincipalKey.Properties.Select(p => p.Name));
        Assert.Equal(new[] { "CountryCode" }, fk.Properties.Select(p => p.Name));
        Assert.Equal("Cities", fk.PrincipalToDependent!.Name);
    }

    [Fact]
    public void CompositeFkToUniqueColumns_KeepsColumnPairing()
    {
        var s = new SchemaBuilder();
        var product = s.Table("products", "Product", "*id", "vendor:text", "sku:text");
        product.Indexes.Add(new IndexMetadata { Name = "ux_vendor_sku", IsUnique = true, Columns = { "vendor", "sku" } });
        var line = s.Table("order_lines", "OrderLine", "*id", "product_sku:text", "product_vendor:text");
        // FK column order follows the constraint (confkey), not the index.
        s.ForeignKey("fk_line_product", line, new[] { "product_sku", "product_vendor" }, product, new[] { "sku", "vendor" });

        var fk = SingleFk(GeneratedModel.Build(s), "OrderLine");

        Assert.Equal(new[] { "Sku", "Vendor" }, fk.PrincipalKey.Properties.Select(p => p.Name));
        Assert.Equal(new[] { "ProductSku", "ProductVendor" }, fk.Properties.Select(p => p.Name));
    }

    [Fact]
    public void OneToOneToUniqueNonPkColumn_UsesGenericHasPrincipalKey()
    {
        var s = new SchemaBuilder();
        var user = s.Table("users", "User", "*id", "email:text");
        user.Indexes.Add(new IndexMetadata { Name = "ux_email", IsUnique = true, Columns = { "email" } });
        var login = s.Table("logins", "Login", "*id", "user_email:text");
        login.Indexes.Add(new IndexMetadata { Name = "ux_user_email", IsUnique = true, Columns = { "user_email" } });
        s.ForeignKey("fk_login_user", login, "user_email", user, "email");

        var model = GeneratedModel.Build(s);
        var fk = SingleFk(model, "Login");

        Assert.True(fk.IsUnique);
        Assert.Equal(new[] { "Email" }, fk.PrincipalKey.Properties.Select(p => p.Name));
        Assert.Contains(".HasPrincipalKey<User>(p => p.Email)", model.Sources["AppDbContext.cs"]);
    }

    [Fact]
    public void FkToPrimaryKey_DoesNotEmitHasPrincipalKey()
    {
        var s = new SchemaBuilder();
        var role = s.Table("roles", "Role", "*id");
        var user = s.Table("users", "User", "*id", "role_id");
        s.ForeignKey("fk_user_role", user, "role_id", role);

        var model = GeneratedModel.Build(s);

        Assert.True(SingleFk(model, "User").PrincipalKey.IsPrimaryKey());
        Assert.DoesNotContain("HasPrincipalKey", model.Sources["AppDbContext.cs"]);
    }

    // Postgres allows an FK to reference a nullable unique column; EF requires key properties to be non-null.
    // The model still builds, but EF treats the column as required: a principal row with a NULL key can't be
    // saved through EF. Known limitation (docs/FINDINGS.md); this test pins the behaviour.
    [Theory]
    [InlineData("text")]
    [InlineData("int4")]
    public void FkToNullableUniqueColumn_BuildsButKeyBecomesRequired(string type)
    {
        var s = new SchemaBuilder();
        var parent = s.Table("parents", "Parent", "*id", $"code:{type}?");
        parent.Indexes.Add(new IndexMetadata { Name = "ux_code", IsUnique = true, Columns = { "code" } });
        var child = s.Table("children", "Child", "*id", $"parent_code:{type}?");
        s.ForeignKey("fk_child_parent", child, "parent_code", parent, "code");

        var model = GeneratedModel.Build(s);

        var key = SingleFk(model, "Child").PrincipalKey;
        Assert.Equal("Code", Assert.Single(key.Properties).Name);
        Assert.False(key.Properties[0].IsNullable);
    }
}
