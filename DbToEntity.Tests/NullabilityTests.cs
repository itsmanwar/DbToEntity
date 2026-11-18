using System.Reflection;
using DbToEntity.Core;
using DbToEntity.Core.Models;
using DbToEntity.Tests.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.EntityFrameworkCore;

namespace DbToEntity.Tests;

/// <summary>F6: nullable reference type annotations in generated code.</summary>
public class NullabilityTests
{
    private static SchemaBuilder Shop()
    {
        var s = new SchemaBuilder();
        var roles = s.Table("roles", "Role", "*id", "name:text");
        var users = s.Table("users", "User", "*id", "email:text", "nickname:text?", "tags:_text?", "avatar:bytea?", "ip:inet?", "age:int4?", "role_id", "manager_id?");
        var profile = s.Table("user_profiles", "UserProfile", "*user_id", "bio:text?");
        s.ForeignKey("fk_users_role", users, "role_id", roles);
        s.ForeignKey("fk_users_manager", users, "manager_id", users);
        s.ForeignKey("fk_profile_user", profile, "user_id", users);
        return s;
    }

    private static readonly NullabilityInfoContext Nullability = new();

    private static NullabilityState State(Type type, string property) =>
        Nullability.Create(type.GetProperty(property) ?? throw new Xunit.Sdk.XunitException($"{type.Name}.{property} missing")).ReadState;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Annotations_MatchTheDatabase(bool fluentOnly)
    {
        var model = GeneratedModel.Build(Shop(), fluentOnly: fluentOnly);
        var user = model.Entity("User").ClrType;

        Assert.Equal(NullabilityState.NotNull, State(user, "Email"));       // NOT NULL text
        Assert.Equal(NullabilityState.Nullable, State(user, "Nickname"));   // nullable text
        Assert.Equal(NullabilityState.Nullable, State(user, "Tags"));       // nullable array
        Assert.Equal(NullabilityState.Nullable, State(user, "Avatar"));     // nullable bytea
        Assert.Equal(NullabilityState.Nullable, State(user, "Ip"));         // nullable IPAddress
        Assert.Equal(NullabilityState.Nullable, State(user, "Age"));        // int? (value type)
        Assert.Equal(NullabilityState.NotNull, State(user, "Role"));        // required FK
        Assert.Equal(NullabilityState.Nullable, State(user, "UserNav"));    // optional FK (self-reference)
        Assert.Equal(NullabilityState.NotNull, State(user, "Users"));       // collection
        Assert.Equal(NullabilityState.Nullable, State(user, "UserProfile")); // one-to-one inverse
        Assert.Equal(NullabilityState.NotNull, State(model.Entity("Role").ClrType, "Users"));
    }

    [Fact]
    public void Collections_AreInitialized()
    {
        var role = Activator.CreateInstance(GeneratedModel.Build(Shop()).Entity("Role").ClrType)!;

        var users = role.GetType().GetProperty("Users")!.GetValue(role);

        Assert.NotNull(users);
    }

    // EF reads NRT annotations to decide requiredness; they must agree with the database in both styles.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EfRequiredness_StillMatchesTheDatabase(bool fluentOnly)
    {
        var s = Shop();
        var model = GeneratedModel.Build(s, fluentOnly: fluentOnly);

        foreach (var table in s.Tables)
        {
            var entity = model.Entity(table.ClassName);
            foreach (var column in table.Columns)
            {
                var property = entity.GetProperties().Single(p => p.GetColumnName() == column.Name);
                Assert.True(column.IsNullable == property.IsNullable, $"{table.Name}.{column.Name}: column nullable={column.IsNullable}, EF nullable={property.IsNullable}");
            }
        }

        var fks = model.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()).ToDictionary(fk => fk.GetConstraintName()!);
        Assert.True(fks["fk_users_role"].IsRequired);
        Assert.False(fks["fk_users_manager"].IsRequired);
        Assert.True(fks["fk_profile_user"].IsRequired);
        Assert.False(fks["fk_profile_user"].IsRequiredDependent); // a user may have no profile
    }

    [Theory]
    [InlineData(NullableContextOptions.Enable)]
    [InlineData(NullableContextOptions.Disable)]
    [InlineData(NullableContextOptions.Warnings)]
    public void NoWarnings_WhateverTheProjectsNullableSetting(NullableContextOptions projectSetting)
    {
        var s = Shop();
        s.Enum("public", "mood", "happy");
        s.Tables.Single(t => t.Name == "users").Columns.Add(new ColumnMetadata { Name = "mood", DataType = "mood", TypeSchema = "public", IsNullable = true });

        var diagnostics = GeneratedModel.Build(s).Diagnostics(projectSetting);

        Assert.Empty(diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString()));
    }

    [Fact]
    public void GeneratedFiles_EnableNullableRightAfterTheHeader()
    {
        var model = GeneratedModel.Build(Shop());

        Assert.All(model.Sources, f => Assert.Contains("// </auto-generated>\r\n#nullable enable\r\n", f.Value));
        Assert.Contains("public string Email { get; set; } = null!;", model.Sources["User.cs"]);
        Assert.Contains("public virtual ICollection<User> Users { get; set; } = new List<User>();", model.Sources["Role.cs"]);
        Assert.Contains("public DbSet<User> Users { get; set; } = null!;", model.Sources["AppDbContext.cs"]);
    }

    // A DbContext generated by an older version has no '#nullable enable'. Updating it adds `= null!` DbSets
    // there; that must not produce warnings either.
    [Fact]
    public void UpdatingAnOlderDbContext_ProducesNoWarnings()
    {
        var s = Shop();
        var v1 = GeneratedModel.Build(s).Sources;
        var oldContext = v1["AppDbContext.cs"].Replace("#nullable enable\r\n", "").Replace(" = null!;", "");

        var coupons = s.Table("coupons", "Coupon", "*id", "code:text");
        ModelPipeline.Prepare(s.Tables, s.Enums);
        foreach (var t in s.Tables) t.Namespace = GeneratedModel.Namespace;
        var generator = new RoslynEntityGenerator();
        var files = new Dictionary<string, string>(v1)
        {
            ["Coupon.cs"] = generator.GenerateEntity(coupons, GeneratedModel.Namespace).Content,
            ["AppDbContext.cs"] = generator.UpdateDbContext(oldContext, new List<TableMetadata> { coupons }, s.Tables, GeneratedModel.ContextName).Content,
        };

        var updated = GeneratedModel.FromSources(files);

        Assert.Empty(updated.Diagnostics(NullableContextOptions.Enable).Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString()));
    }
}
