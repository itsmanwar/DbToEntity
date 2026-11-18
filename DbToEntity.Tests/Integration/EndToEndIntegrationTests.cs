using System.Collections;
using System.Net;
using DbToEntity.Core;
using DbToEntity.Core.Models;
using DbToEntity.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DbToEntity.Tests.Integration;

/// <summary>
/// Real database → metadata → generated code → compiled → used through EF Core against the same database.
/// Proves the mappings work for actual reads and writes, not just in EF's model.
/// </summary>
[Collection(PostgresCollection.Name)]
public class EndToEndIntegrationTests
{
    private readonly PostgresFixture _db;

    public EndToEndIntegrationTests(PostgresFixture db) => _db = db;

    private static GeneratedModel? _model;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    // What `db2entity generate` does after connecting (defaults: all schemas, pluralize, attributes + fluent).
    private async Task<GeneratedModel> ModelAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_model != null) return _model;
            var provider = new PostgresMetadataProvider();
            var tables = await provider.GetTablesAsync(_db.ConnectionString, null);
            var enums = await provider.GetEnumsAsync(_db.ConnectionString);
            ModelPipeline.ExcludeTables(tables, null);
            ModelPipeline.ResolveClassNames(tables);
            return _model = GeneratedModel.Build(tables, enums);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static object New(GeneratedModel model, string className, params (string Property, object? Value)[] values)
    {
        var entity = Activator.CreateInstance(model.ClrType(className))!;
        foreach (var (property, value) in values) Set(entity, property, value);
        return entity;
    }

    private static void Set(object entity, string property, object? value)
    {
        var info = entity.GetType().GetProperty(property) ?? throw new Xunit.Sdk.XunitException($"{entity.GetType().Name}.{property} missing");
        var target = Nullable.GetUnderlyingType(info.PropertyType) ?? info.PropertyType;
        info.SetValue(entity, value is string s && target.IsEnum ? Enum.Parse(target, s) : value);
    }

    private static object? Get(object entity, string property) => entity.GetType().GetProperty(property)!.GetValue(entity);

    private static List<object> All(DbContext context, Type type) =>
        ((IEnumerable)typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!.MakeGenericMethod(type).Invoke(context, null)!)
        .Cast<object>().ToList();

    [DockerFact]
    public async Task WholeDatabase_GeneratesCompilesAndBuildsTheModel()
    {
        var model = await ModelAsync();
        var classes = model.Model.GetEntityTypes().Select(e => e.ClrType.Name).ToHashSet();

        Assert.Empty(model.Warnings); // every column type in the schema is supported
        Assert.Contains("User", classes);
        Assert.Contains("RoleCount", classes); // materialized view, now with columns
        Assert.Contains("Truck", classes);
        Assert.DoesNotContain("EFMigrationsHistory", classes);
        Assert.DoesNotContain(classes, c => c.Contains("2026")); // partition child
        Assert.Contains("/// User roles", model.Sources["Role.cs"]);
        Assert.Contains("npgsql.MapEnum<OrderStatus>(\"order_status\", \"sales\");", model.Sources["AppDbContext.cs"]);
    }

    // The type fixes in practice: timestamp (Kind=Unspecified), jsonb from string, enum + enum array,
    // citext, text[] — all written through EF and read back.
    [DockerFact]
    public async Task WriteAndRead_EnumsArraysJsonbAndTimestamp()
    {
        var model = await ModelAsync();
        var created = new DateTime(2026, 11, 18, 10, 30, 0, DateTimeKind.Unspecified);

        await using (var context = model.CreateContext(_db.ConnectionString))
        {
            var role = New(model, "Role", ("Name", "writers"));
            var user = New(model, "User",
                ("Email", "Ada@Example.com"), ("Nickname", "ada"), ("Mood", "VerySad"),
                ("Tags", new[] { "a", "b" }), ("CreatedAt", created), ("Payload", "{\"likes\": [1, 2]}"),
                ("SeenAt", new DateTime(2026, 11, 18, 5, 0, 0, DateTimeKind.Utc)), ("Role", role));
            var moods = Array.CreateInstance(model.ClrType("User").GetProperty("Mood")!.PropertyType, 2);
            moods.SetValue(Enum.Parse(moods.GetType().GetElementType()!, "Happy"), 0);
            moods.SetValue(Enum.Parse(moods.GetType().GetElementType()!, "Ok"), 1);
            Set(user, "Moods", moods);
            context.Add(user);
            await context.SaveChangesAsync();
        }

        await using (var context = model.CreateContext(_db.ConnectionString))
        {
            var user = All(context, model.ClrType("User")).Single(u => (string)Get(u, "Nickname")! == "ada");

            Assert.Equal("VerySad", Get(user, "Mood")!.ToString());
            Assert.Equal(new[] { "Happy", "Ok" }, ((Array)Get(user, "Moods")!).Cast<object>().Select(m => m.ToString()));
            Assert.Equal(new[] { "a", "b" }, (string[])Get(user, "Tags")!);
            Assert.Equal(created, (DateTime)Get(user, "CreatedAt")!);
            Assert.Contains("\"likes\"", (string)Get(user, "Payload")!);
            Assert.Equal("Ada@Example.com", Get(user, "Email"));
        }

        // The database saw real enum/jsonb values, not text/ints.
        await using var check = new Npgsql.NpgsqlConnection(_db.ConnectionString);
        await check.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand("SELECT mood::text, payload->'likes'->>1, (email = 'ADA@example.com') FROM users WHERE nickname = 'ada'", check);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("very sad", reader.GetString(0)); // [PgName] label
        Assert.Equal("2", reader.GetString(1));        // stored as jsonb
        Assert.True(reader.GetBoolean(2));             // citext comparison
    }

    [DockerFact]
    public async Task ReadAllMappedTypes()
    {
        var model = await ModelAsync();
        await using var context = model.CreateContext(_db.ConnectionString);

        var row = All(context, model.ClrType("AllType")).Single();

        var nulls = model.ClrType("AllType").GetProperties().Where(p => p.GetValue(row) == null).Select(p => p.Name).ToList();
        Assert.Empty(nulls); // every column was read into its mapped type
        Assert.Equal(new DateTime(2026, 11, 18, 10, 30, 0), (DateTime)Get(row, "CTimestamp")!);
        Assert.Equal(IPAddress.Parse("10.0.0.1"), Get(row, "CInet"));
        Assert.Equal(new[] { 1, 2, 3 }, (int[])Get(row, "CInt4Arr")!);
        Assert.Equal("v", ((Dictionary<string, string>)Get(row, "CHstore")!)["k"]);
        Assert.Equal(new TimeOnly(10, 30), Get(row, "CTime"));
        Assert.Equal(TimeSpan.FromHours(26), Get(row, "CInterval"));
        Assert.Contains("\"b\"", (string)Get(row, "CJsonb")!);
    }

    [DockerFact]
    public async Task OneToOne_AlternateKey_AndOtherSchema_RoundTrip()
    {
        var model = await ModelAsync();

        await using (var context = model.CreateContext(_db.ConnectionString))
        {
            var role = New(model, "Role", ("Name", "readers"));
            var user = New(model, "User", ("Email", "bob@example.com"), ("Nickname", "bob"), ("Role", role));
            Set(user, "UserProfile", New(model, "UserProfile", ("Bio", "hi")));      // shared-PK one-to-one
            Set(user, "UserSetting", New(model, "UserSetting", ("Theme", "dark")));  // unique-FK one-to-one
            var country = New(model, "Country", ("IsoCode", "IN"));
            var city = New(model, "City", ("Name", "Kolkata"), ("Country", country)); // FK to iso_code
            var order = New(model, "Order", ("Status", "Shipped"), ("Total", 12.50m), ("PlacedOn", new DateOnly(2026, 11, 18)), ("User", user));
            context.AddRange(user, city, order);
            await context.SaveChangesAsync();
        }

        await using (var context = model.CreateContext(_db.ConnectionString))
        {
            var city = All(context, model.ClrType("City")).Single(c => (string)Get(c, "Name")! == "Kolkata");
            Assert.Equal("IN", Get(city, "CountryCode"));

            var order = All(context, model.ClrType("Order")).Single(o => (decimal)Get(o, "Total")! == 12.50m);
            Assert.Equal("Shipped", Get(order, "Status")!.ToString());

            var profiles = All(context, model.ClrType("UserProfile"));
            Assert.Contains(profiles, p => (string?)Get(p, "Bio") == "hi");
        }
    }

    [DockerFact]
    public async Task DeleteBehaviors_MatchTheDatabase()
    {
        var model = await ModelAsync();
        object role, user;

        await using (var context = model.CreateContext(_db.ConnectionString))
        {
            role = New(model, "Role", ("Name", "temp"));
            user = New(model, "User", ("Email", "carol@example.com"), ("Nickname", "carol"), ("Role", role));
            Set(user, "UserProfile", New(model, "UserProfile", ("Bio", "bye")));
            context.Add(user);
            await context.SaveChangesAsync();
        }

        // RESTRICT: deleting a role that still has users fails in the database.
        await using (var context = model.CreateContext(_db.ConnectionString))
        {
            context.Remove(All(context, model.ClrType("Role")).Single(r => (string)Get(r, "Name")! == "temp"));
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }

        // CASCADE: deleting the user removes the profile.
        await using (var context = model.CreateContext(_db.ConnectionString))
        {
            var id = (int)Get(user, "Id")!;
            context.Remove(All(context, model.ClrType("User")).Single(u => (int)Get(u, "Id")! == id));
            await context.SaveChangesAsync();
            Assert.DoesNotContain(All(context, model.ClrType("UserProfile")), p => (int)Get(p, "UserId")! == id);
        }
    }
}
