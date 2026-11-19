using DbToEntity.Core;
using NpgsqlTypes;

namespace DbToEntity.Tests;

/// <summary>Query construction for PostgresMetadataProvider (no database needed).</summary>
public class MetadataQueryTests
{
    [Fact]
    public void TableNames_ArePassedAsArrayParameter_NeverInSqlText()
    {
        var hostile = "users'); DROP TABLE users; --";

        using var cmd = PostgresMetadataProvider.CreateTableQuery("public", new[] { "orders", hostile });

        Assert.DoesNotContain("DROP", cmd.CommandText);
        Assert.DoesNotContain("orders", cmd.CommandText);
        Assert.Contains("c.relname = ANY(@names)", cmd.CommandText);

        var names = cmd.Parameters["names"];
        Assert.Equal(NpgsqlDbType.Array | NpgsqlDbType.Text, names.NpgsqlDbType);
        Assert.Equal(new[] { "orders", hostile }, (string[])names.Value!);
        Assert.Equal("public", cmd.Parameters["schema"].Value);
    }

    [Fact]
    public void NoTableNames_AddsNoNameFilter()
    {
        using var cmd = PostgresMetadataProvider.CreateTableQuery(null, null);

        Assert.DoesNotContain("@names", cmd.CommandText);
        Assert.False(cmd.Parameters.Contains("names"));
        Assert.Equal(DBNull.Value, cmd.Parameters["schema"].Value);
    }

    [Fact]
    public void EmptyTableNames_AddsNoNameFilter()
    {
        using var cmd = PostgresMetadataProvider.CreateTableQuery("public", Array.Empty<string>());

        Assert.DoesNotContain("@names", cmd.CommandText);
    }
}
