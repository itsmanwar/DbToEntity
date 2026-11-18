using DbToEntity.Core;
using DbToEntity.Tests.Infrastructure;

namespace DbToEntity.Tests;

public class EntityNamingTests
{
    [Fact]
    public void SingleFk_NavigationIsTargetClass_InverseIsPluralSource()
    {
        var s = new SchemaBuilder();
        var role = s.Table("roles", "Role", "*id");
        var user = s.Table("users", "User", "*id", "role_id");
        var fk = s.ForeignKey("fk_user_role", user, "role_id", role);

        EntityNaming.ResolveNavigationNames(s.Tables);

        Assert.Equal("Role", fk.NavigationName);
        Assert.Equal("Users", fk.InverseNavigationName);
    }

    [Fact]
    public void MultipleFksToSameTarget_AreNamedAfterTheirColumns()
    {
        var s = new SchemaBuilder();
        var file = s.Table("uploaded_files", "UploadedFile", "*id");
        var p = s.Table("pensioners", "Pensioner", "*id", "photo_file_id", "sign_file_id");
        var photo = s.ForeignKey("fk_photo", p, "photo_file_id", file);
        var sign = s.ForeignKey("fk_sign", p, "sign_file_id", file);

        EntityNaming.ResolveNavigationNames(s.Tables);

        Assert.Equal(("PhotoFile", "PensionerPhotoFiles"), (photo.NavigationName, photo.InverseNavigationName));
        Assert.Equal(("SignFile", "PensionerSignFiles"), (sign.NavigationName, sign.InverseNavigationName));
    }

    [Fact]
    public void SelfReference_NavigationAvoidsClassName()
    {
        var s = new SchemaBuilder();
        var cat = s.Table("categories", "Category", "*id", "parent_id?");
        var fk = s.ForeignKey("fk_parent", cat, "parent_id", cat);

        EntityNaming.ResolveNavigationNames(s.Tables);

        Assert.Equal("CategoryNav", fk.NavigationName);
        Assert.Equal("Categories", fk.InverseNavigationName);
    }

    [Fact]
    public void NavigationCollidingWithColumn_GetsSuffix()
    {
        var s = new SchemaBuilder();
        var role = s.Table("roles", "Role", "*id", "users"); // column "users" collides with the inverse collection
        var user = s.Table("users", "User", "*id", "role_id", "role:text"); // column "role" collides with the reference nav
        var fk = s.ForeignKey("fk_user_role", user, "role_id", role);

        EntityNaming.ResolveNavigationNames(s.Tables);

        Assert.Equal("RoleNav", fk.NavigationName);
        Assert.Equal("UsersCollection", fk.InverseNavigationName);
    }

    [Fact]
    public void RepeatedCollisions_GetNumberedSuffix()
    {
        var s = new SchemaBuilder();
        var role = s.Table("roles", "Role", "*id");
        var user = s.Table("users", "User", "*id", "role_id", "role:text", "role_nav:text");
        var fk = s.ForeignKey("fk_user_role", user, "role_id", role);

        EntityNaming.ResolveNavigationNames(s.Tables);

        Assert.Equal("RoleNav2", fk.NavigationName);
    }

    [Theory]
    [InlineData("first_name", "User", "FirstName")]
    [InlineData("user", "User", "UserMember")]
    [InlineData("2fa_code", "User", "_2faCode")]
    [InlineData("e-mail", "User", "EMail")]
    public void GetPropertyName(string column, string className, string expected) =>
        Assert.Equal(expected, EntityNaming.GetPropertyName(column, className));
}
