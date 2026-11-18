using DbToEntity.Core;
using DbToEntity.Core.Models;
using DbToEntity.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DbToEntity.Tests;

/// <summary>Quick wins: --no-pluralize (Q2), --fluent-only (Q3), database comments (Q4).</summary>
public class OptionsTests
{
    private static SchemaBuilder Shop()
    {
        var s = new SchemaBuilder();
        var roles = s.Table("roles", "Role", "*id", "name:varchar(50)");
        roles.Indexes.Add(new IndexMetadata { Name = "ux_roles_name", IsUnique = true, Columns = { "name" } });
        var users = s.Table("users", "User", "*id", "email:text", "nickname:text?", "role_id", "created_at:timestamp");
        users.Indexes.Add(new IndexMetadata { Name = "ix_users_email_nick", Columns = { "email", "nickname" } });
        var profile = s.Table("user_profiles", "UserProfile", "*user_id", "bio:text?");
        s.ForeignKey("fk_users_role", users, "role_id", roles, onDelete: ReferentialAction.Restrict);
        s.ForeignKey("fk_profile_user", profile, "user_id", users, onDelete: ReferentialAction.Cascade);
        s.View("active_users", "ActiveUser", "id", "email:text");
        return s;
    }

    // Everything EF knows about the mapping, as comparable text.
    private static string[] Fingerprint(IModel model) =>
        model.GetEntityTypes().OrderBy(e => e.ClrType.Name).SelectMany(e =>
            new[] { $"{e.ClrType.Name}: table={e.GetTableName()} view={e.GetViewName()} key={string.Join(",", e.FindPrimaryKey()?.Properties.Select(p => p.Name) ?? [])}/{e.FindPrimaryKey()?.GetName()}" }
            .Concat(e.GetProperties().Select(p => $"  {p.Name}: {p.GetColumnName()} {p.GetColumnType()} null={p.IsNullable} max={p.GetMaxLength()}"))
            .Concat(e.GetIndexes().Select(i => $"  index {i.GetDatabaseName()} ({string.Join(",", i.Properties.Select(p => p.Name))}) unique={i.IsUnique}"))
            .Concat(e.GetForeignKeys().Select(fk => $"  fk {fk.GetConstraintName()} {fk.DependentToPrincipal?.Name}->{fk.PrincipalEntityType.ClrType.Name}.{fk.PrincipalToDependent?.Name} unique={fk.IsUnique} delete={fk.DeleteBehavior}"))
        ).ToArray();

    [Fact]
    public void FluentOnly_EntitiesHaveNoAttributes()
    {
        var model = GeneratedModel.Build(Shop(), fluentOnly: true);

        foreach (var (name, source) in model.Sources.Where(f => f.Key != "AppDbContext.cs"))
            Assert.DoesNotMatch(@"^\s*\[", string.Join("\n", source.Split('\n').Where(l => !l.TrimStart().StartsWith("//"))));
        Assert.Contains(".HasIndex(e => new { e.Email, e.Nickname }).HasDatabaseName(\"ix_users_email_nick\")", model.Sources["AppDbContext.cs"]);
    }

    // The two styles must describe exactly the same model.
    [Fact]
    public void FluentOnly_ProducesTheSameModelAsAttributes()
    {
        var withAttributes = Fingerprint(GeneratedModel.Build(Shop()).Model);
        var fluentOnly = Fingerprint(GeneratedModel.Build(Shop(), fluentOnly: true).Model);

        Assert.Equal(withAttributes, fluentOnly);
    }

    [Fact]
    public void NoPluralize_KeepsTableNamesForClassesDbSetsAndCollections()
    {
        var s = new SchemaBuilder();
        var tables = new List<TableMetadata>
        {
            new() { Schema = "public", Name = "users", Columns = { new() { Name = "id", DataType = "int4" }, new() { Name = "role_id", DataType = "int4" } }, PrimaryKeys = { "id" } },
            new() { Schema = "public", Name = "role", Columns = { new() { Name = "id", DataType = "int4" } }, PrimaryKeys = { "id" } },
        };
        s.Tables.AddRange(tables);
        ModelPipeline.ResolveClassNames(tables, singularize: false);
        s.ForeignKey("fk_users_role", tables[0], "role_id", tables[1]);

        var model = GeneratedModel.Build(s, pluralize: false);

        Assert.Equal(new[] { "Role", "Users" }, tables.Select(t => t.ClassName).OrderBy(n => n));
        Assert.Contains("public DbSet<Users> Users { get; set; }", model.Sources["AppDbContext.cs"]);
        Assert.Contains("public DbSet<Role> Role { get; set; }", model.Sources["AppDbContext.cs"]);
        Assert.Equal("Users", model.Entity("Role").GetNavigations().Single().Name); // collection, not "Userses"
    }

    [Fact]
    public void Pluralize_IsTheDefault()
    {
        var tables = new List<TableMetadata> { new() { Schema = "public", Name = "users" }, new() { Schema = "public", Name = "categories" } };

        ModelPipeline.ResolveClassNames(tables);

        Assert.Equal(new[] { "User", "Category" }, tables.Select(t => t.ClassName));
    }

    [Fact]
    public void DbSetNames_DoNotHideDbContextMembers()
    {
        var s = new SchemaBuilder();
        s.Table("database", "Database", "*id");
        s.Table("model", "Model", "*id");

        var model = GeneratedModel.Build(s, pluralize: false);

        Assert.Contains("public DbSet<Database> DatabaseSet { get; set; }", model.Sources["AppDbContext.cs"]);
        Assert.Contains("public DbSet<Model> ModelSet { get; set; }", model.Sources["AppDbContext.cs"]);
    }

    [Fact]
    public void Comments_BecomeHasCommentAndXmlDocs()
    {
        var s = Shop();
        var users = s.Tables.Single(t => t.Name == "users");
        users.Comment = "Registered users.\nSoft-deleted rows are kept.";
        users.Columns.Single(c => c.Name == "email").Comment = "Login e-mail; must be < 255 chars & unique";
        s.Tables.Single(t => t.Name == "active_users").Comment = "Users seen in the last 30 days";

        var model = GeneratedModel.Build(s);
        var user = model.Entity("User");

        Assert.Equal("Registered users.\nSoft-deleted rows are kept.", user.GetComment());
        Assert.Equal("Login e-mail; must be < 255 chars & unique", user.FindProperty("Email")!.GetComment());

        var source = model.Sources["User.cs"];
        Assert.Contains("/// Registered users.\r\n", source);
        Assert.Contains("/// Soft-deleted rows are kept.\r\n", source);
        Assert.Contains("/// Login e-mail; must be &lt; 255 chars &amp; unique", source); // XML-escaped
        Assert.Contains("/// Users seen in the last 30 days", model.Sources["ActiveUser.cs"]); // views: doc comment only
    }

    [Fact]
    public void Comments_AreKeptInFluentOnlyMode()
    {
        var s = Shop();
        s.Tables.Single(t => t.Name == "users").Columns.Single(c => c.Name == "email").Comment = "Login e-mail";

        var model = GeneratedModel.Build(s, fluentOnly: true);

        Assert.Contains("/// Login e-mail", model.Sources["User.cs"]);
        Assert.Equal("Login e-mail", model.Entity("User").FindProperty("Email")!.GetComment());
    }
}
