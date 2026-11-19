using DbToEntity.Core.Models;
using DbToEntity.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

namespace DbToEntity.Tests;

/// <summary>
/// GENERATED ALWAYS AS (...) columns and identity columns. Without configuration EF writes them, and PostgreSQL
/// rejects the statement ("cannot insert a non-DEFAULT value into column").
/// </summary>
public class GeneratedColumnTests
{
    private static (SchemaBuilder Schema, TableMetadata Table) LineItems()
    {
        var s = new SchemaBuilder();
        var t = s.Table("line_items", "LineItem", "*id", "line_no:int8", "price:numeric", "qty", "total:numeric?", "half:numeric?");
        ColumnMetadata Col(string name) => t.Columns.Single(c => c.Name == name);
        Col("id").Identity = IdentityKind.ByDefault;
        Col("line_no").Identity = IdentityKind.Always;
        Col("total").ComputedSql = "(price * (qty)::numeric)";
        Col("half").ComputedSql = "(price / 2)";
        Col("half").IsStoredComputed = false; // VIRTUAL, PostgreSQL 18+
        return (s, t);
    }

    [Fact]
    public void ComputedColumns_AreReadOnlyForEf()
    {
        var model = GeneratedModel.Build(LineItems().Schema);
        var entity = model.Entity("LineItem");

        var total = entity.FindProperty("Total")!;
        Assert.Equal("(price * (qty)::numeric)", total.GetComputedColumnSql());
        Assert.True(total.GetIsStored());
        Assert.Equal(ValueGenerated.OnAddOrUpdate, total.ValueGenerated);
        Assert.Null(total.GetDefaultValueSql());

        var half = entity.FindProperty("Half")!;
        Assert.Equal("(price / 2)", half.GetComputedColumnSql());
        Assert.False(half.GetIsStored());
    }

    [Fact]
    public void IdentityColumns_UseTheMatchingStrategy_AlsoForNonKeys()
    {
        var model = GeneratedModel.Build(LineItems().Schema);
        var entity = model.Entity("LineItem");

        Assert.Equal(NpgsqlValueGenerationStrategy.IdentityByDefaultColumn, entity.FindProperty("Id")!.GetValueGenerationStrategy());
        var lineNo = entity.FindProperty("LineNo")!;
        Assert.Equal(NpgsqlValueGenerationStrategy.IdentityAlwaysColumn, lineNo.GetValueGenerationStrategy());
        Assert.Equal(ValueGenerated.OnAdd, lineNo.ValueGenerated);
        Assert.Equal(ValueGenerated.Never, entity.FindProperty("Price")!.ValueGenerated);
    }

    [Fact]
    public void FluentOnly_AlsoConfiguresThem()
    {
        var model = GeneratedModel.Build(LineItems().Schema, fluentOnly: true);
        var entity = model.Entity("LineItem");

        Assert.Equal("(price * (qty)::numeric)", entity.FindProperty("Total")!.GetComputedColumnSql());
        Assert.Equal(NpgsqlValueGenerationStrategy.IdentityAlwaysColumn, entity.FindProperty("LineNo")!.GetValueGenerationStrategy());
    }
}
