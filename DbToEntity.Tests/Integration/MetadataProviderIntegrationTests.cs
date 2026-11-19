using DbToEntity.Core;
using DbToEntity.Core.Models;
using DbToEntity.Tests.Infrastructure;
using Npgsql;

namespace DbToEntity.Tests.Integration;

/// <summary>PostgresMetadataProvider against a real PostgreSQL 17 (Integration/schema.sql).</summary>
[Collection(PostgresCollection.Name)]
public class MetadataProviderIntegrationTests
{
    private readonly PostgresFixture _db;

    public MetadataProviderIntegrationTests(PostgresFixture db) => _db = db;

    private async Task<List<TableMetadata>> LoadAsync(IEnumerable<string>? tables = null) =>
        await new PostgresMetadataProvider().GetTablesAsync(_db.ConnectionString, null, tables);

    private static TableMetadata Table(List<TableMetadata> tables, string name, string schema = "public") =>
        tables.SingleOrDefault(t => t.Name == name && t.Schema == schema)
        ?? throw new Xunit.Sdk.XunitException($"{schema}.{name} not loaded. Loaded: {string.Join(", ", tables.Select(t => $"{t.Schema}.{t.Name}"))}");

    [DockerFact]
    public async Task Tables_ViewsAndPartitions()
    {
        var tables = await LoadAsync();

        Assert.True(Table(tables, "events").IsPartitioned);
        Assert.DoesNotContain(tables, t => t.Name == "events_2026"); // partition children are not generated
        Assert.Contains(tables, t => t.Name == "trucks");             // classic inheritance children are
        Assert.Equal(ObjectType.View, Table(tables, "active_users").Type);
        Assert.Equal(ObjectType.MaterializedView, Table(tables, "role_counts").Type);
        Assert.Contains(tables, t => t.Name == "orders" && t.Schema == "sales");
    }

    [DockerFact]
    public async Task Columns_TypesSchemasAndLengths()
    {
        var users = Table(await LoadAsync(), "users");
        ColumnMetadata Col(string name) => users.Columns.Single(c => c.Name == name);

        Assert.Equal(("mood", "public"), (Col("mood").DataType, Col("mood").TypeSchema));
        Assert.Equal("_mood", Col("moods").DataType);
        Assert.Equal("citext", Col("email").DataType);
        Assert.False(Col("email").IsNullable);
        Assert.True(Col("nickname").IsNullable);
        Assert.Equal("timestamp", Col("created_at").DataType);
        Assert.Equal(new[] { "id" }, users.PrimaryKeys);

        var orders = Table(await LoadAsync(), "orders", "sales");
        Assert.Equal(("order_status", "sales"), (orders.Columns.Single(c => c.Name == "status").DataType, orders.Columns.Single(c => c.Name == "status").TypeSchema));

        var roles = Table(await LoadAsync(), "roles");
        Assert.Equal(50, roles.Columns.Single(c => c.Name == "name").MaxLength);
        Assert.Equal(2, Table(await LoadAsync(), "countries").Columns.Single(c => c.Name == "iso_code").MaxLength);
    }

    // Known gap (FINDINGS 1.4): information_schema.columns does not list materialized view columns.
    [DockerFact]
    public async Task MaterializedView_HasColumns()
    {
        var view = Table(await LoadAsync(), "role_counts");

        Assert.Equal(new[] { "role_id", "n" }, view.Columns.Select(c => c.Name));
    }

    [DockerFact]
    public async Task DomainColumns_ResolveToTheirBaseType()
    {
        var email = Table(await LoadAsync(), "contacts").Columns.Single(c => c.Name == "email");

        Assert.Equal(("varchar", "pg_catalog", 120), (email.DataType, email.TypeSchema, email.MaxLength));
        Assert.False(email.IsNullable); // NOT NULL on the domain
    }

    [DockerFact]
    public async Task Comments()
    {
        var tables = await LoadAsync();

        Assert.Equal("User roles", Table(tables, "roles").Comment);
        Assert.Equal("Display name & <key>", Table(tables, "roles").Columns.Single(c => c.Name == "name").Comment);
        Assert.Equal("Users with a nickname", Table(tables, "active_users").Comment);
        Assert.Null(Table(tables, "users").Comment);
    }

    [DockerFact]
    public async Task ForeignKeys_DeleteActionsAndTargets()
    {
        var tables = await LoadAsync();
        ForeignKeyMetadata Fk(string table, string column, string schema = "public") =>
            Table(tables, table, schema).ForeignKeys.Single(f => f.SourceColumns.SequenceEqual(new[] { column }));

        Assert.Equal(ReferentialAction.Restrict, Fk("users", "role_id").OnDelete);
        Assert.Equal(ReferentialAction.SetNull, Fk("users", "manager_id").OnDelete);
        Assert.Equal(ReferentialAction.Cascade, Fk("user_profiles", "user_id").OnDelete);
        Assert.Equal(ReferentialAction.NoAction, Fk("cities", "country_code").OnDelete);
        Assert.Equal(ReferentialAction.SetDefault, Fk("orders", "user_id", "sales").OnDelete);

        Assert.Equal(new[] { "iso_code" }, Fk("cities", "country_code").TargetColumns);
        Assert.Equal(("public", "users"), (Fk("orders", "user_id", "sales").TargetSchema, Fk("orders", "user_id", "sales").TargetTable));
        Assert.Contains(Table(tables, "users").ReferencingForeignKeys, f => f.SourceTable == "orders"); // cross-schema inverse
    }

    [DockerFact]
    public async Task Indexes_OrderPartialExpressionAndPartitioned()
    {
        var tables = await LoadAsync();
        var users = Table(tables, "users");
        IndexMetadata Index(TableMetadata t, string name) => t.Indexes.Single(i => i.Name == name);

        Assert.Equal(new[] { "nickname", "created_at" }, Index(users, "ix_users_nick_created").Columns);
        Assert.True(Index(users, "ux_users_partial").IsPartial);
        Assert.True(Index(users, "ux_users_expr").HasExpressions);
        Assert.True(Index(users, "ux_users_email").IsUnique);
        Assert.False(Index(users, "ux_users_email").IsPartial || Index(users, "ux_users_email").HasExpressions);
        Assert.Contains(Table(tables, "user_settings").Indexes, i => i.IsUnique && i.Columns.SequenceEqual(new[] { "user_id" })); // UNIQUE constraint
        Assert.Contains(Table(tables, "events").Indexes, i => i.Name == "ix_events_kind"); // partitioned parent
    }

    [DockerFact]
    public async Task Enums_AllSchemasInLabelOrder()
    {
        var enums = await new PostgresMetadataProvider().GetEnumsAsync(_db.ConnectionString);

        Assert.Equal(new[] { "happy", "very sad", "ok" }, enums.Single(e => e.Schema == "public" && e.Name == "mood").Labels);
        Assert.Equal(new[] { "pending", "shipped" }, enums.Single(e => e.Schema == "sales" && e.Name == "order_status").Labels);
    }

    [DockerFact]
    public async Task TableFilter_HandlesQuotesAndCannotInject()
    {
        var quoted = await LoadAsync(new[] { "it's" });
        var hostile = await LoadAsync(new[] { "x'); DROP TABLE roles; --" });

        Assert.Equal("it's", Assert.Single(quoted).Name);
        Assert.Empty(hostile);

        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT to_regclass('public.roles') IS NOT NULL", conn);
        Assert.True((bool)(await cmd.ExecuteScalarAsync())!); // roles still exists
    }

    [DockerFact]
    public async Task GeneratedAndIdentityColumns()
    {
        var items = Table(await LoadAsync(), "line_items");
        ColumnMetadata Col(string name) => items.Columns.Single(c => c.Name == name);

        Assert.Equal(IdentityKind.ByDefault, Col("id").Identity);
        Assert.Equal(IdentityKind.Always, Col("line_no").Identity);
        Assert.Equal(IdentityKind.None, Col("price").Identity);
        Assert.Contains("price", Col("total").ComputedSql);
        Assert.True(Col("total").IsStoredComputed);
        Assert.Null(Col("total").DefaultValue); // the generation expression is not a default
        Assert.Null(Col("price").ComputedSql);
        Assert.Equal("1", Col("qty").DefaultValue);
    }
}
